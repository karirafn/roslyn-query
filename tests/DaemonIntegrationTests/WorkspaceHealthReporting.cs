using System.Diagnostics;

using Shouldly;

using AppHostLocator = roslyn_query.Tests.ColdStartIntegrationTests.AppHostLocator;

namespace roslyn_query.Tests.DaemonIntegrationTests;

/// <summary>
/// End-to-end integration coverage for daemon-path workspace health reporting (issue #86).
///
/// Partial-path decision: NETSDK1100-induced project drops are host-dependent and
/// would be flaky in CI (the project file alone does not deterministically drop on all
/// hosts and SDK versions). The unit tiers (LoadHealthTests, CommandDispatcherTests/
/// LoadHealthReporting, CommandDispatcherTests/ListProjectsCommand) already cover the
/// partial path with injected LoadHealth. This layer covers only the complete-load path
/// end-to-end through the daemon, which is the authoritative proof that the daemon wires
/// health collection and reporting correctly for a clean fixture.
/// </summary>
public sealed class WorkspaceHealthReporting : IAsyncLifetime
{
    private const int DeadlineSeconds = 120;

    private readonly string _fixtureSlnxPath;
    private readonly string _fixtureDir;
    private readonly string _stateDir;
    private readonly string _fileName;
    private readonly IReadOnlyList<string> _prefixArgs;
    private readonly bool _skip;

    public WorkspaceHealthReporting()
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

        // Unique per-class fixture copy so the derived pipe name does not collide with other
        // test classes running in parallel against the same solution path.
        _fixtureDir = Path.Combine(Path.GetTempPath(), $"rq-health-fix-{Guid.NewGuid():N}");
        _stateDir = Path.Combine(Path.GetTempPath(), $"rq-health-state-{Guid.NewGuid():N}");
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
    public async Task WhenListProjectsRunsAgainstCleanFixture_StderrContainsWorkspaceComplete()
    {
        // Arrange
        if (_skip)
        {
            // Graceful skip when binary absent — consistent with ADR 0002.
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(DeadlineSeconds));

        ProcessStartInfo startInfo = BuildClientStartInfo(["list-projects", _fixtureSlnxPath]);

        // Act — runs through the daemon path (spawns daemon on cold start, then serves via pipe).
        using Process client = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the roslyn-query client process.");

        string stderr;
        string stdout;
        try
        {
            // Read both streams concurrently to prevent pipe-buffer deadlocks.
            Task<string> readStdout = client.StandardOutput.ReadToEndAsync(cts.Token);
            Task<string> readStderr = client.StandardError.ReadToEndAsync(cts.Token);
            await Task.WhenAll(readStdout, readStderr);
            stdout = await readStdout;
            stderr = await readStderr;
            await client.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new ShouldAssertException(
                $"Process did not complete within {DeadlineSeconds}s. " +
                "The workspace load may have hung or the daemon did not respond.");
        }

        // Assert
        client.ExitCode.ShouldBe(0, $"list-projects should succeed against the MinimalSolution fixture; stderr: {stderr}");
        stderr.ShouldContain(
            "workspace: complete",
            Case.Sensitive,
            $"daemon should report workspace: complete for a clean fixture load; full stderr: {stderr}");

        // Stdout must contain at least one project line — proving the daemon served the command.
        stdout.ShouldNotBeEmpty("list-projects should emit at least one project line on stdout");

        // Stdout must NOT contain any partial notice — the complete path emits nothing extra there.
        stdout.Contains("workspace: partial", StringComparison.Ordinal).ShouldBeFalse(
            "the complete-load path must not embed a partial notice in stdout");
    }

    [Fact]
    public async Task WhenNonListProjectsCommandRunsAgainstCleanFixture_StderrContainsNoPartialNotice()
    {
        // Arrange
        if (_skip)
        {
            // Graceful skip when binary absent — consistent with ADR 0002.
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(DeadlineSeconds));

        // Warm up first: ensure the daemon is running and loaded before the assertion command.
        int warmupExit = await RunClientAsync(["list-projects", _fixtureSlnxPath], cts.Token);
        warmupExit.ShouldBe(0, "warm-up list-projects should succeed");

        // Use find-refs as the non-list-projects command — it returns 0 with no output on a
        // clean fixture that has no references to the queried symbol.
        ProcessStartInfo startInfo = BuildClientStartInfo(["find-refs", "NonExistentType", _fixtureSlnxPath]);

        // Act — daemon is already loaded; this exercises the hot path through HandleConnectionAsync.
        using Process client = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the roslyn-query client process.");

        string stderr;
        try
        {
            Task<string> readStdout = client.StandardOutput.ReadToEndAsync(cts.Token);
            Task<string> readStderr = client.StandardError.ReadToEndAsync(cts.Token);
            await Task.WhenAll(readStdout, readStderr);
            stderr = await readStderr;
            await client.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new ShouldAssertException(
                $"Process did not complete within {DeadlineSeconds}s; full stderr so far is unavailable.");
        }

        // Assert — a complete load must NOT emit a partial notice on non-list-projects commands.
        stderr.Contains("workspace: partial", StringComparison.Ordinal).ShouldBeFalse(
            $"complete-load daemon must not emit a partial notice; full stderr: {stderr}");
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

    private async Task<int> RunClientAsync(
        IReadOnlyList<string> commandArgs,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = BuildClientStartInfo(commandArgs);

        using Process client = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start roslyn-query client.");

        // Read both streams concurrently to prevent pipe-buffer deadlocks.
        Task<string> stdoutTask = client.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = client.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);
        await client.WaitForExitAsync(cancellationToken);
        return client.ExitCode;
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
