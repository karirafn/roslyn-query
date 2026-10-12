using System.Diagnostics;

using Shouldly;

using AppHostLocator = roslyn_query.Tests.ColdStartIntegrationTests.AppHostLocator;

namespace roslyn_query.Tests.DaemonIntegrationTests;

/// <summary>
/// Real-apphost integration test: a batch run against a perpetually-loading daemon exits
/// with code 75 (EX_TEMPFAIL) and emits no command headers to stdout.
///
/// Exercises the true cold-start spawn path: no pre-start — the client spawns the daemon
/// via DaemonCommandRunner's WaitForReadyAsync path (NotRunning → spawn → retry loop).
///
/// The daemon is held in the Loading state by ROSLYN_QUERY_TEST_LOAD_DELAY_MS and the
/// client's loading deadline is shortened via ROSLYN_QUERY_TEST_LOADING_DEADLINE_MS so
/// the deadline expires before the daemon becomes ready — proving the batch runner
/// aborts before writing any "=== command ===" header lines. Both env vars are set on
/// the client so they are inherited by the daemon the client spawns.
/// </summary>
public sealed class BatchDeadline : IAsyncLifetime
{
    private const int TestDeadlineSeconds = 60;
    private const int ClientDeadlineMs = 500;
    private const int DaemonLoadDelayMs = 30_000;

    private readonly string _fixtureSlnxPath;
    private readonly string _fixtureDir;
    private readonly string _stateDir;
    private readonly string _batchFilePath;
    private readonly string _fileName;
    private readonly IReadOnlyList<string> _prefixArgs;
    private readonly bool _skip;

    public BatchDeadline()
    {
        if (!AppHostLocator.TryLocate(out string fileName, out IReadOnlyList<string> prefixArgs))
        {
            _skip = true;
            _fileName = string.Empty;
            _prefixArgs = [];
            _fixtureSlnxPath = string.Empty;
            _fixtureDir = string.Empty;
            _stateDir = string.Empty;
            _batchFilePath = string.Empty;
            return;
        }

        _fileName = fileName;
        _prefixArgs = prefixArgs;

        _fixtureDir = Path.Combine(Path.GetTempPath(), $"rq-batchdeadline-fix-{Guid.NewGuid():N}");
        _stateDir = Path.Combine(Path.GetTempPath(), $"rq-batchdeadline-state-{Guid.NewGuid():N}");
        _fixtureSlnxPath = Path.Combine(_fixtureDir, "MinimalSolution.slnx");

        // A batch file with one command — should never execute because the deadline fires first.
        _batchFilePath = Path.Combine(Path.GetTempPath(), $"rq-batch-{Guid.NewGuid():N}.txt");
    }

    public async Task InitializeAsync()
    {
        if (_skip)
        {
            return;
        }

        CopyFixtureToDir(_fixtureDir);
        Directory.CreateDirectory(_stateDir);

        // Write the batch command file: one list-projects command.
        await File.WriteAllTextAsync(
            _batchFilePath,
            $"list-projects {_fixtureSlnxPath}{Environment.NewLine}");

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

        if (File.Exists(_batchFilePath))
        {
            File.Delete(_batchFilePath);
        }
    }

    [Fact]
    public async Task WhenBatchRunAgainstLoadingDaemon_ExitsTransientWithNoHeaders()
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
        ProcessStartInfo startInfo = BuildClientStartInfo(
            ["batch", _fixtureSlnxPath, _batchFilePath]);
        startInfo.EnvironmentVariables["ROSLYN_QUERY_TEST_ENABLE_SEAMS"] = "1";
        startInfo.EnvironmentVariables["ROSLYN_QUERY_TEST_LOAD_DELAY_MS"] =
            DaemonLoadDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.EnvironmentVariables["ROSLYN_QUERY_TEST_LOADING_DEADLINE_MS"] =
            ClientDeadlineMs.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Act — cold start: no daemon pre-running. Client spawns it, deadline expires.
        using Process client = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start roslyn-query client.");

        string[] outputs = await Task.WhenAll(
            client.StandardError.ReadToEndAsync(cts.Token),
            client.StandardOutput.ReadToEndAsync(cts.Token));
        string stderr = outputs[0];
        string stdout = outputs[1];
        await client.WaitForExitAsync(cts.Token);

        // Assert — batch must exit 75 when the loading deadline expires.
        client.ExitCode.ShouldBe(
            75,
            $"expected transient exit (75) when batch loading deadline expires; stderr: {stderr}");

        // No command headers should be written — the batch runner aborts before the loop.
        stdout.ShouldBeEmpty(
            $"expected empty stdout (no headers emitted before deadline); stdout: {stdout}");

        // The "no commands run" message must appear on stderr.
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
