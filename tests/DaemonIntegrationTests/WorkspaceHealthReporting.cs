using System.Diagnostics;

using Shouldly;

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

    private readonly DaemonTestFixture _fixture = new("health");

    public async Task InitializeAsync()
    {
        if (_fixture.Skip)
        {
            return;
        }

        _fixture.CopyFixture();

        // Ensure cold start: stop any stale daemon for this fixture path.
        await _fixture.StopDaemonAsync();
    }

    public async Task DisposeAsync()
    {
        if (_fixture.Skip)
        {
            return;
        }

        await _fixture.StopDaemonAsync();
        _fixture.Cleanup();
    }

    [Fact]
    public async Task WhenListProjectsRunsAgainstCleanFixture_StderrContainsWorkspaceComplete()
    {
        // Arrange
        if (_fixture.Skip)
        {
            // Graceful skip when binary absent — consistent with ADR 0002.
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(DeadlineSeconds));

        ProcessStartInfo startInfo = _fixture.BuildClientStartInfo(["list-projects", _fixture.FixtureSlnxPath]);

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
        if (_fixture.Skip)
        {
            // Graceful skip when binary absent — consistent with ADR 0002.
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(DeadlineSeconds));

        // Warm up first: ensure the daemon is running and loaded before the assertion command.
        int warmupExit = await _fixture.RunClientAsync(["list-projects", _fixture.FixtureSlnxPath], cts.Token);
        warmupExit.ShouldBe(0, "warm-up list-projects should succeed");

        // Use find-refs as the non-list-projects command — it returns 0 with no output on a
        // clean fixture that has no references to the queried symbol.
        ProcessStartInfo startInfo = _fixture.BuildClientStartInfo(
            ["find-refs", "NonExistentType", _fixture.FixtureSlnxPath]);

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
}
