using System.Diagnostics;

using Shouldly;

using AppHostLocator = roslyn_query.Tests.ColdStartIntegrationTests.AppHostLocator;

namespace roslyn_query.Tests.DaemonIntegrationTests;

/// <summary>
/// Real-apphost integration test: when the loading deadline elapses before the daemon
/// becomes ready, the client exits with code 75 (EX_TEMPFAIL / transient).
///
/// Exercises the true cold-start spawn path: no pre-start — the client spawns the daemon
/// via DaemonCommandRunner's NotRunning → spawn → retry loop.
///
/// Uses two seam env vars (gated by ROSLYN_QUERY_TEST_ENABLE_SEAMS=1) set on the client,
/// so they are inherited by the daemon the client spawns:
///   ROSLYN_QUERY_TEST_LOAD_DELAY_MS       — daemon-side: keeps daemon in Loading state
///   ROSLYN_QUERY_TEST_LOADING_DEADLINE_MS — client-side: shortens the loading deadline
///
/// The daemon is held in Loading by a very long delay so the client's shortened deadline
/// is guaranteed to expire before the daemon becomes ready.
/// </summary>
public sealed class LoadingDeadline : IAsyncLifetime
{
    private const int TestDeadlineSeconds = 60;

    // Client deadline: short enough that it expires before the daemon load delay.
    private const int ClientDeadlineMs = 500;

    // Daemon load delay: long enough that the client deadline definitely expires first.
    private const int DaemonLoadDelayMs = 30_000;

    private readonly string _fixtureSlnxPath;
    private readonly string _fixtureDir;
    private readonly string _stateDir;
    private readonly string _socketDir;
    private readonly string _fileName;
    private readonly IReadOnlyList<string> _prefixArgs;
    private readonly bool _skip;

    public LoadingDeadline()
    {
        if (!AppHostLocator.TryLocate(out string fileName, out IReadOnlyList<string> prefixArgs))
        {
            _skip = true;
            _fileName = string.Empty;
            _prefixArgs = [];
            _fixtureSlnxPath = string.Empty;
            _fixtureDir = string.Empty;
            _stateDir = string.Empty;
            _socketDir = string.Empty;
            return;
        }

        _fileName = fileName;
        _prefixArgs = prefixArgs;

        // Unique per-class fixture copy so the derived pipe name does not collide with other test classes.
        _fixtureDir = Path.Combine(Path.GetTempPath(), $"rq-loaddeadline-fix-{Guid.NewGuid():N}");
        _stateDir = Path.Combine(Path.GetTempPath(), $"rq-loaddeadline-state-{Guid.NewGuid():N}");
        // Per-class socket dir so PrepareSocketDirectory always creates a fresh 0700 directory.
        _socketDir = Path.Combine(Path.GetTempPath(), $"rq-loaddeadline-sock-{Guid.NewGuid():N}");
        _fixtureSlnxPath = Path.Combine(_fixtureDir, "MinimalSolution.slnx");
    }

    public async Task InitializeAsync()
    {
        if (_skip)
        {
            return;
        }

        CopyFixtureToDir(_fixtureDir);
        Directory.CreateDirectory(_stateDir);

        // Ensure cold start: stop any stale daemon for this fixture path.
        await StopDaemonAsync();
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
        CleanupDirectory(_socketDir);
    }

    [Fact]
    public async Task WhenLoadingDeadlineExpires_ClientExitsWithTransient()
    {
        // Arrange
        if (_skip)
        {
            // Graceful skip when binary absent — consistent with ADR 0002.
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(TestDeadlineSeconds));

        // Seam env vars on the client: client spawns the daemon, daemon inherits LOAD_DELAY.
        // The client's LOADING_DEADLINE_MS (500ms) fires before the daemon's 30s load delay.
        ProcessStartInfo startInfo = BuildClientStartInfo(["list-projects", _fixtureSlnxPath]);
        startInfo.EnvironmentVariables["ROSLYN_QUERY_TEST_ENABLE_SEAMS"] = "1";
        startInfo.EnvironmentVariables["ROSLYN_QUERY_TEST_LOAD_DELAY_MS"] =
            DaemonLoadDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.EnvironmentVariables["ROSLYN_QUERY_TEST_LOADING_DEADLINE_MS"] =
            ClientDeadlineMs.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Act — cold start: no daemon pre-running. Client spawns it, deadline expires.
        using Process client = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start roslyn-query client.");

        // Read stderr and stdout concurrently to prevent pipe-buffer deadlocks.
        string[] outputs = await Task.WhenAll(
            client.StandardError.ReadToEndAsync(cts.Token),
            client.StandardOutput.ReadToEndAsync(cts.Token));
        string stderr = outputs[0];
        await client.WaitForExitAsync(cts.Token);

        // Assert — client must exit with code 75 (EX_TEMPFAIL) when the deadline expires.
        client.ExitCode.ShouldBe(
            75,
            $"expected transient exit (75) when loading deadline expires; stderr: {stderr}");

        // The client must have printed the deadline-exceeded message.
        stderr.ShouldContain(
            "daemon still loading",
            Case.Insensitive,
            $"expected deadline message; full stderr: {stderr}");
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
        startInfo.EnvironmentVariables["ROSLYN_QUERY_SOCKET_DIR"] = _socketDir;
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
        stopInfo.EnvironmentVariables["ROSLYN_QUERY_SOCKET_DIR"] = _socketDir;

        using Process stop = Process.Start(stopInfo)
            ?? throw new InvalidOperationException("Failed to start roslyn-query to stop daemon.");

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
        await stop.WaitForExitAsync(cts.Token);
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
