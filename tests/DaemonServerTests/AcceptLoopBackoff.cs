using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonServerTests;

/// <summary>
/// Verifies the accept-loop bind-failure backoff and peer-rejection separation:
///  - Threshold consecutive bind failures cause the loop to break (RunAsync completes naturally).
///  - Fewer-than-threshold failures followed by a success reset the counter; daemon keeps serving.
///  - A peer-rejection UnauthorizedAccessException does not count toward the exit threshold.
///
/// Uses injectable seams (SetForcedCreateFailuresForTests, SetBindFailureBackoffForTests,
/// SetSuppressEnvironmentExitForTests) so tests run with deterministic, fast failures.
/// </summary>
public sealed class AcceptLoopBackoff : IDisposable
{
    // Very short backoff so tests run in ms, not seconds.
    private static readonly TimeSpan TestBackoff = TimeSpan.FromMilliseconds(5);

    // Hard deadline for each test so a bug does not hang CI.
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(20);

    public void Dispose()
    {
        // Clear all seams so they do not bleed into other tests.
        DaemonServer.SetForcedCreateFailuresForTests(0);
        DaemonServer.SetBindFailureBackoffForTests(null);
        DaemonServer.SetIdleTimeoutForTests(null);
        DaemonServer.SetSuppressEnvironmentExitForTests(false);
        PipeProtocol.SetSocketDirectoryOverrideForTests(null);
    }

    [Fact]
    public async Task WhenMaxConsecutiveBindFailuresReached_AcceptLoopExits()
    {
        // Arrange
        string socketDir = Path.Combine(Path.GetTempPath(), $"rq-test-backoff-{Guid.NewGuid():N}");
        PipeProtocol.SetSocketDirectoryOverrideForTests(socketDir);
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.sln");

        DaemonServer.SetForcedCreateFailuresForTests(DaemonServer.MaxConsecutiveBindFailures);
        DaemonServer.SetBindFailureBackoffForTests(TestBackoff);
        DaemonServer.SetIdleTimeoutForTests(TimeSpan.FromSeconds(30));
        DaemonServer.SetSuppressEnvironmentExitForTests(true);

        // Act — RunAsync should exit when MaxConsecutiveBindFailures is reached.
        Task daemonTask = Task.Run(
            () => DaemonServer.RunAsync(fakeSolutionPath),
            CancellationToken.None);

        bool exitedBeforeDeadline;
        try
        {
            await daemonTask.WaitAsync(TestDeadline);
            exitedBeforeDeadline = true;
        }
        catch (TimeoutException)
        {
            exitedBeforeDeadline = false;
        }

        // Assert — loop must have exited cleanly (break, not Environment.Exit) so RunAsync completed.
        exitedBeforeDeadline.ShouldBeTrue(
            "accept loop should exit after MaxConsecutiveBindFailures consecutive failures");
    }

    [Fact]
    public async Task WhenFewerThanThresholdFailuresThenSuccess_CounterResets_DaemonKeepsServing()
    {
        // Arrange
        string socketDir = Path.Combine(Path.GetTempPath(), $"rq-test-backoff-{Guid.NewGuid():N}");
        PipeProtocol.SetSocketDirectoryOverrideForTests(socketDir);
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.sln");

        // Inject fewer-than-threshold failures so the loop recovers and keeps serving.
        int failCount = DaemonServer.MaxConsecutiveBindFailures - 1;
        DaemonServer.SetForcedCreateFailuresForTests(failCount);
        DaemonServer.SetBindFailureBackoffForTests(TestBackoff);
        DaemonServer.SetIdleTimeoutForTests(TimeSpan.FromSeconds(15));
        DaemonServer.SetSuppressEnvironmentExitForTests(true);

        using CancellationTokenSource externalCts = new(TestDeadline);

        // Act — start the daemon; it will fail failCount times then succeed.
        Task daemonTask = Task.Run(
            () => DaemonServer.RunAsync(fakeSolutionPath, externalCts.Token),
            CancellationToken.None);

        // Connect a client — retry until the daemon has exhausted the forced failures and is
        // accepting. A bounded retry loop avoids flakiness when the backoff window is active.
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);
        using System.IO.Pipes.NamedPipeClientStream client = new(
            ".",
            pipeName,
            System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous);

        using CancellationTokenSource connectCts = new(TimeSpan.FromSeconds(10));
        while (!client.IsConnected && !connectCts.Token.IsCancellationRequested)
        {
            try
            {
                await client.ConnectAsync(connectCts.Token);
            }
            catch (IOException) when (!connectCts.Token.IsCancellationRequested)
            {
                await Task.Delay(50, connectCts.Token);
            }
        }

        client.IsConnected.ShouldBeTrue("daemon did not become ready within 10 s");

        await PipeProtocol.WriteRequestAsync(
            client,
            PipeProtocol.ProtocolVersion,
            PipeProtocol.ClientInformationalVersion,
            ["list-projects", fakeSolutionPath],
            externalCts.Token);

        DaemonResponse response =
            await PipeProtocol.ReadResponseEnvelopeAsync(client, externalCts.Token);

        // Assert — daemon is still alive and responded after recovering from forced failures.
        response.Status.ShouldBeOneOf(
            ResponseStatus.Loading,
            ResponseStatus.Result,
            ResponseStatus.Reloading);

        // Cleanup
        await externalCts.CancelAsync();
        await daemonTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WhenPeerRejectedByUnauthorizedAccess_CounterNotIncremented_LoopContinues()
    {
        // Arrange
        string socketDir = Path.Combine(Path.GetTempPath(), $"rq-test-backoff-{Guid.NewGuid():N}");
        PipeProtocol.SetSocketDirectoryOverrideForTests(socketDir);
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.sln");

        // Inject exactly threshold forced peer-rejection errors (via the same seam to simulate
        // UnauthorizedAccessException from WaitForConnectionAsync). These must NOT count toward
        // the exit threshold. Daemon should keep serving after them.
        DaemonServer.SetForcedPeerRejectionFailuresForTests(DaemonServer.MaxConsecutiveBindFailures);
        DaemonServer.SetBindFailureBackoffForTests(TestBackoff);
        DaemonServer.SetIdleTimeoutForTests(TimeSpan.FromSeconds(15));
        DaemonServer.SetSuppressEnvironmentExitForTests(true);

        using CancellationTokenSource externalCts = new(TestDeadline);

        // Act — start the daemon; it will reject peers without counting toward threshold.
        Task daemonTask = Task.Run(
            () => DaemonServer.RunAsync(fakeSolutionPath, externalCts.Token),
            CancellationToken.None);

        // Connect a legitimate client — retry until the daemon has exhausted the forced peer
        // rejections and is accepting. A bounded retry loop avoids flakiness when the daemon
        // is still cycling through forced rejections.
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);
        using System.IO.Pipes.NamedPipeClientStream client = new(
            ".",
            pipeName,
            System.IO.Pipes.PipeDirection.InOut,
            System.IO.Pipes.PipeOptions.Asynchronous);

        using CancellationTokenSource connectCts = new(TimeSpan.FromSeconds(10));
        while (!client.IsConnected && !connectCts.Token.IsCancellationRequested)
        {
            try
            {
                await client.ConnectAsync(connectCts.Token);
            }
            catch (IOException) when (!connectCts.Token.IsCancellationRequested)
            {
                await Task.Delay(50, connectCts.Token);
            }
        }

        client.IsConnected.ShouldBeTrue("daemon did not become ready within 10 s");

        await PipeProtocol.WriteRequestAsync(
            client,
            PipeProtocol.ProtocolVersion,
            PipeProtocol.ClientInformationalVersion,
            ["list-projects", fakeSolutionPath],
            externalCts.Token);

        DaemonResponse response =
            await PipeProtocol.ReadResponseEnvelopeAsync(client, externalCts.Token);

        // Assert — daemon still alive after threshold-count peer rejections.
        response.Status.ShouldBeOneOf(
            ResponseStatus.Loading,
            ResponseStatus.Result,
            ResponseStatus.Reloading);

        // Cleanup
        await externalCts.CancelAsync();
        await daemonTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
