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

    // Performs a connect→write→read exchange with a resilient retry loop bounded by the
    // supplied CancellationToken. A fresh NamedPipeClientStream is created per attempt so
    // that a broken-pipe IOException (server disposed the pipe during a forced-rejection
    // window) or an InvalidOperationException (write to a disconnected pipe) does not leave
    // the test stuck with an unusable client object.
    //
    // Returns the first DaemonResponse received, or throws TimeoutException if the token is
    // cancelled before a successful exchange. A dead daemon never responds, so the loop times
    // out and the test fails — the retry absorbs only the pre-recovery broken-pipe window,
    // not a genuine daemon death.
    private static async Task<DaemonResponse> PerformResilientExchangeAsync(
        string pipeName,
        string solutionPath,
        CancellationToken deadlineToken)
    {
        while (!deadlineToken.IsCancellationRequested)
        {
            System.IO.Pipes.NamedPipeClientStream client = new(
                ".",
                pipeName,
                System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous);

            try
            {
                // Connect — retry on IOException (pipe not yet available) with a short delay.
                while (!client.IsConnected && !deadlineToken.IsCancellationRequested)
                {
                    try
                    {
                        await client.ConnectAsync(deadlineToken);
                    }
                    catch (IOException) when (!deadlineToken.IsCancellationRequested)
                    {
                        await Task.Delay(50, deadlineToken);
                    }
                }

                if (!client.IsConnected)
                {
                    // Token cancelled during connect — exit loop; caller handles the failure.
                    break;
                }

                // Write the request then read the response. Either call can throw IOException
                // or InvalidOperationException if the server disposed the pipe during a
                // forced-rejection window (the connect→write window is the source of the race).
                await PipeProtocol.WriteRequestAsync(
                    client,
                    PipeProtocol.ProtocolVersion,
                    PipeProtocol.ClientInformationalVersion,
                    ["list-projects", solutionPath],
                    deadlineToken);

                DaemonResponse response =
                    await PipeProtocol.ReadResponseEnvelopeAsync(client, deadlineToken);

                return response;
            }
            catch (IOException) when (!deadlineToken.IsCancellationRequested)
            {
                // Broken pipe from server dropping the connection during a forced-rejection
                // window — dispose this client and retry with a fresh one after a short delay.
                await client.DisposeAsync();
                await Task.Delay(50, deadlineToken);
                continue;
            }
            catch (InvalidOperationException) when (!deadlineToken.IsCancellationRequested)
            {
                // Write to a disconnected pipe — same recovery path as IOException above.
                await client.DisposeAsync();
                await Task.Delay(50, deadlineToken);
                continue;
            }
            finally
            {
                // Dispose on every non-continue path (success or cancellation).
                // The catch arms above already disposed on the retry paths.
                await client.DisposeAsync();
            }
        }

        throw new TimeoutException(
            "Daemon did not serve a request within the allotted deadline.");
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

        // An external CTS is passed so the orphaned background-load task (which starts loading a
        // non-existent solution path) observes cancellation before Dispose() resets suppress.
        // The accept loop breaks after MaxConsecutiveBindFailures (~25 ms) — long before
        // TestDeadline — so this token does NOT cause the break; it only tears down the orphan.
        using CancellationTokenSource externalCts = new(TestDeadline);

        // Act — RunAsync should exit when MaxConsecutiveBindFailures is reached.
        Task daemonTask = Task.Run(
            () => DaemonServer.RunAsync(fakeSolutionPath, externalCts.Token),
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

        // Cancel so the orphaned background-load task takes the clean OperationCanceledException
        // path rather than the generic catch that would call Environment.Exit when suppress is reset.
        // RunAsync has already returned (the accept loop broke), so this only affects the load task.
        await externalCts.CancelAsync();
        await daemonTask;

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

        // Connect a client and perform the request/response exchange. The resilient helper
        // retries with a fresh client on broken-pipe or disconnected-pipe errors that can
        // occur if the server disposes the pipe during a bind-failure backoff window.
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);
        using CancellationTokenSource connectCts = new(TimeSpan.FromSeconds(10));

        DaemonResponse response =
            await PerformResilientExchangeAsync(pipeName, fakeSolutionPath, connectCts.Token);

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

        // Connect a legitimate client and perform the request/response exchange. During the
        // forced-rejection window the server disposes the pipe without accepting, so the
        // client's connection can succeed at the socket level but immediately break when
        // the server tears it down. The resilient helper absorbs these broken-pipe IOExceptions
        // by creating a fresh NamedPipeClientStream on each retry.
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);
        using CancellationTokenSource connectCts = new(TimeSpan.FromSeconds(10));

        DaemonResponse response =
            await PerformResilientExchangeAsync(pipeName, fakeSolutionPath, connectCts.Token);

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
