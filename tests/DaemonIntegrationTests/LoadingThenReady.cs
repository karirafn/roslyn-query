using System.Diagnostics;

using Shouldly;

using AppHostLocator = roslyn_query.Tests.ColdStartIntegrationTests.AppHostLocator;

namespace roslyn_query.Tests.DaemonIntegrationTests;

/// <summary>
/// Real-apphost integration test: a cold daemon replies "loading" for the first client,
/// then becomes ready. The client must eventually succeed (exit 0) and print the
/// "daemon: loading workspace" progress message to stderr.
///
/// Uses ROSLYN_QUERY_TEST_LOAD_DELAY_MS (gated by ROSLYN_QUERY_TEST_ENABLE_SEAMS=1) to
/// keep the daemon in the Loading state long enough for the client to observe it, without
/// requiring a real slow workspace load.
///
/// The daemon is started explicitly in InitializeAsync with the seam env vars so the
/// client connects to an already-loading daemon (avoiding the PID-file race that would
/// cause DaemonCommandRunner to fall back to RunDirect before the daemon is ready).
/// </summary>
public sealed class LoadingThenReady : IAsyncLifetime
{
    private const int DeadlineSeconds = 120;

    // Load delay: long enough for the client to poll at least once in the Loading state,
    // short enough to resolve well within the 120s test deadline.
    private const int LoadDelayMs = 5_000;

    // How long to wait for the daemon PID file after spawn (daemon writes it synchronously
    // on startup before loading begins, so 10 seconds is very conservative).
    private const int DaemonStartTimeoutMs = 10_000;

    private readonly string _fixtureSlnxPath;
    private readonly string _fixtureDir;
    private readonly string _stateDir;
    private readonly string _fileName;
    private readonly IReadOnlyList<string> _prefixArgs;
    private readonly bool _skip;

    public LoadingThenReady()
    {
        if (!AppHostLocator.TryLocate(out string fileName, out IReadOnlyList<string> prefixArgs))
        {
            _skip = true;
            _fileName = string.Empty;
            _prefixArgs = [];
            _fixtureSlnxPath = string.Empty;
            _fixtureDir = string.Empty;
            _stateDir = string.Empty;
            return;
        }

        _fileName = fileName;
        _prefixArgs = prefixArgs;

        // Unique per-class fixture copy so the derived pipe name does not collide with other test classes.
        _fixtureDir = Path.Combine(Path.GetTempPath(), $"rq-loadingready-fix-{Guid.NewGuid():N}");
        _stateDir = Path.Combine(Path.GetTempPath(), $"rq-loadingready-state-{Guid.NewGuid():N}");
        _fixtureSlnxPath = Path.Combine(_fixtureDir, "MinimalSolution.slnx");
    }

    public async Task InitializeAsync()
    {
        if (_skip)
        {
            return;
        }

        CopyFixtureToDir(_fixtureDir);

        // Ensure cold start: stop any stale daemon for this fixture path.
        await StopDaemonAsync();

        // Pre-start the daemon with the load-delay seam so the client connects to a daemon
        // already in Loading state, rather than relying on the spawn-then-check path which
        // can race with PID file creation and fall back to RunDirect.
        await StartDaemonWithLoadDelayAsync();
    }

    public async Task DisposeAsync()
    {
        if (_skip)
        {
            return;
        }

        await StopDaemonAsync();
        CleanupDirectory(_fixtureDir);
        CleanupDirectory(_stateDir);
    }

    [Fact]
    public async Task WhenDaemonIsLoading_ClientWaitsAndEventuallySucceeds()
    {
        // Arrange
        if (_skip)
        {
            // Graceful skip when binary absent — consistent with ADR 0002.
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(DeadlineSeconds));

        ProcessStartInfo startInfo = BuildClientStartInfo(["list-projects", _fixtureSlnxPath]);

        // Act — the daemon is already running in Loading state (started in InitializeAsync).
        // The client connects, receives Loading replies, waits, and eventually gets Result.
        using Process client = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start roslyn-query client.");

        // Read stderr and stdout concurrently to prevent pipe-buffer deadlocks.
        string[] outputs = await Task.WhenAll(
            client.StandardError.ReadToEndAsync(cts.Token),
            client.StandardOutput.ReadToEndAsync(cts.Token));
        string stderr = outputs[0];
        string stdout = outputs[1];
        await client.WaitForExitAsync(cts.Token);

        // Assert — client must succeed eventually (not exit 75).
        client.ExitCode.ShouldBe(0, $"client should succeed after the load delay; stderr: {stderr}");

        // The client must have printed at least one loading progress message.
        stderr.ShouldContain(
            "daemon: loading workspace",
            Case.Insensitive,
            $"expected loading progress message; full stderr: {stderr}");

        // Stdout should contain project output from list-projects.
        stdout.ShouldNotBeEmpty("list-projects should emit at least one line of output");
    }

    /// <summary>
    /// Starts the daemon process with the load-delay seam env vars and waits until the daemon
    /// has written its PID file (which happens synchronously before the load task starts).
    /// </summary>
    private async Task StartDaemonWithLoadDelayAsync()
    {
        ProcessStartInfo daemonInfo = new()
        {
            FileName = _fileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string arg in _prefixArgs)
        {
            daemonInfo.ArgumentList.Add(arg);
        }

        daemonInfo.ArgumentList.Add("--daemon");
        daemonInfo.ArgumentList.Add(_fixtureSlnxPath);

        // Inject the load delay seam: the daemon will sleep this many ms before loading,
        // keeping it in the Loading state long enough for the client to observe it.
        daemonInfo.EnvironmentVariables["ROSLYN_QUERY_TEST_ENABLE_SEAMS"] = "1";
        daemonInfo.EnvironmentVariables["ROSLYN_QUERY_TEST_LOAD_DELAY_MS"] =
            LoadDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        daemonInfo.EnvironmentVariables["ROSLYN_QUERY_STATE_DIR"] = _stateDir;

        using Process daemon = Process.Start(daemonInfo)
            ?? throw new InvalidOperationException("Failed to start roslyn-query daemon.");

        // Close the parent's pipe ends so the daemon does not wait for stdin input.
        daemon.StandardInput.Close();
        daemon.StandardOutput.BaseStream.Close();
        daemon.StandardError.BaseStream.Close();

        // Wait for the PID file — the daemon writes it synchronously before starting the
        // background load task, so its presence means the pipe server is starting.
        string stateDir = _stateDir;
        string slnxPath = _fixtureSlnxPath;

        using CancellationTokenSource cts = new(DaemonStartTimeoutMs);
        string pidFilePath = DaemonPidFilePath(stateDir, slnxPath);

        while (!File.Exists(pidFilePath))
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, cts.Token);
        }
    }

    private ProcessStartInfo BuildClientStartInfo(IReadOnlyList<string> commandArgs)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = _fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string arg in _prefixArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (string arg in commandArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.EnvironmentVariables["ROSLYN_QUERY_STATE_DIR"] = _stateDir;
        return startInfo;
    }

    private async Task StopDaemonAsync()
    {
        ProcessStartInfo stopInfo = new()
        {
            FileName = _fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string arg in _prefixArgs)
        {
            stopInfo.ArgumentList.Add(arg);
        }

        stopInfo.ArgumentList.Add("daemon");
        stopInfo.ArgumentList.Add("stop");
        stopInfo.ArgumentList.Add(_fixtureSlnxPath);
        stopInfo.EnvironmentVariables["ROSLYN_QUERY_STATE_DIR"] = _stateDir;

        using Process stop = Process.Start(stopInfo)
            ?? throw new InvalidOperationException("Failed to start roslyn-query to stop daemon.");

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
        await stop.WaitForExitAsync(cts.Token);
    }

    /// <summary>
    /// Returns the PID file path for the given solution, mirroring PipeProtocol.DerivePidFilePath
    /// so the test can poll for daemon startup without referencing the internal API.
    /// The naming must match PipeProtocol exactly:
    /// <c>{stateDir}/roslyn-query-{SHA256(normalizedPath)[..32]}.pid</c>.
    /// </summary>
    private static string DaemonPidFilePath(string stateDir, string slnxPath)
    {
        // PipeProtocol.Hash: full path, trailing-separator-trimmed, lower-hex SHA256 first 32 chars.
        // PipeProtocol uses Convert.ToHexStringLower which is lowercase-invariant hex.
        string fullPath = System.IO.Path.TrimEndingDirectorySeparator(Path.GetFullPath(slnxPath));
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fullPath));
        // Build lowercase hex without ToLowerInvariant (CA1308): hex chars are 0-9 and a-f.
        string hexUpper = Convert.ToHexString(hash);
        char[] hexLower = new char[hexUpper.Length];
        for (int i = 0; i < hexUpper.Length; i++)
        {
            hexLower[i] = hexUpper[i] is >= 'A' and <= 'F'
                ? (char)(hexUpper[i] + 32)
                : hexUpper[i];
        }

        string stem = "roslyn-query-" + new string(hexLower, 0, 32);
        return Path.Combine(stateDir, stem + ".pid");
    }

    private static void CopyFixtureToDir(string targetDir)
    {
        string sourceDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MinimalSolution");
        Directory.CreateDirectory(targetDir);

        foreach (string sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDir, sourceFile);
            string destFile = Path.Combine(targetDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(sourceFile, destFile, overwrite: true);
        }
    }

    private static void CleanupDirectory(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        foreach (string subDir in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length))
        {
            Directory.Delete(subDir);
        }

        Directory.Delete(dir);
    }
}
