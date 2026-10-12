using System.Diagnostics;

using Shouldly;

using AppHostLocator = roslyn_query.Tests.ColdStartIntegrationTests.AppHostLocator;

namespace roslyn_query.Tests.DaemonIntegrationTests;

/// <summary>
/// Real-apphost integration test: a cold daemon replies "loading" for the first client,
/// then becomes ready. The client must eventually succeed (exit 0) and print the
/// "daemon: loading workspace" progress message to stderr.
///
/// Exercises the true cold-start spawn path: no pre-start — the client spawns the daemon
/// via DaemonCommandRunner's NotRunning → spawn → retry loop.
///
/// Uses ROSLYN_QUERY_TEST_LOAD_DELAY_MS (gated by ROSLYN_QUERY_TEST_ENABLE_SEAMS=1) to
/// keep the daemon in the Loading state long enough for the client to observe it, without
/// requiring a real slow workspace load. The env vars are set on the client process so
/// they are inherited by the daemon the client spawns.
///
/// This test is the authoritative proof that the cold-start race (PID-file not yet written
/// when the runner checks liveness) does not cause RunDirect fallback.
/// </summary>
public sealed class LoadingThenReady : IAsyncLifetime
{
    private const int DeadlineSeconds = 120;

    // Load delay: long enough for the client to poll at least once in the Loading state,
    // short enough to resolve well within the 120s test deadline.
    private const int LoadDelayMs = 5_000;

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
    }

    [Fact]
    public async Task WhenDaemonIsLoading_ClientSpawnsDaemonWaitsAndEventuallySucceeds()
    {
        // Arrange
        if (_skip)
        {
            // Graceful skip when binary absent — consistent with ADR 0002.
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(DeadlineSeconds));

        // The client process has ROSLYN_QUERY_TEST_ENABLE_SEAMS=1 and LOAD_DELAY set.
        // When the client spawns the daemon, the daemon inherits these vars and stays in
        // Loading state for LoadDelayMs — proving the client waits through Loading rather
        // than falling back to RunDirect on the cold-start spawn path.
        ProcessStartInfo startInfo = BuildClientStartInfo(["list-projects", _fixtureSlnxPath]);
        startInfo.EnvironmentVariables["ROSLYN_QUERY_TEST_ENABLE_SEAMS"] = "1";
        startInfo.EnvironmentVariables["ROSLYN_QUERY_TEST_LOAD_DELAY_MS"] =
            LoadDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Act — cold start: no daemon pre-running. Client spawns it, observes Loading, waits.
        using Process client = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start roslyn-query client.");

        // Read stderr and stdout concurrently to prevent pipe-buffer deadlocks.
        string[] outputs = await Task.WhenAll(
            client.StandardError.ReadToEndAsync(cts.Token),
            client.StandardOutput.ReadToEndAsync(cts.Token));
        string stderr = outputs[0];
        string stdout = outputs[1];
        await client.WaitForExitAsync(cts.Token);

        // Assert — client must succeed eventually (not exit 75 or RunDirect with wrong binary).
        client.ExitCode.ShouldBe(0, $"client should succeed after the load delay; stderr: {stderr}");

        // The client must have printed at least one loading progress message,
        // proving it observed the Loading state rather than bypassing with RunDirect.
        stderr.ShouldContain(
            "daemon: loading workspace",
            Case.Insensitive,
            $"expected loading progress message; full stderr: {stderr}");

        // Stdout should contain project output from list-projects.
        stdout.ShouldNotBeEmpty("list-projects should emit at least one line of output");
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
