using System.Diagnostics;

using Shouldly;

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

    private readonly DaemonTestFixture _fixture = new("loadingready");

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
    public async Task WhenDaemonIsLoading_ClientSpawnsDaemonWaitsAndEventuallySucceeds()
    {
        // Arrange
        if (_fixture.Skip)
        {
            // Graceful skip when binary absent — consistent with ADR 0002.
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(DeadlineSeconds));

        // The client process has ROSLYN_QUERY_TEST_ENABLE_SEAMS=1 and LOAD_DELAY set.
        // When the client spawns the daemon, the daemon inherits these vars and stays in
        // Loading state for LoadDelayMs — proving the client waits through Loading rather
        // than falling back to RunDirect on the cold-start spawn path.
        ProcessStartInfo startInfo = _fixture.BuildClientStartInfo(
            ["list-projects", _fixture.FixtureSlnxPath]);
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
}
