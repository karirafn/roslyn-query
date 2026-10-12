using System.IO.Pipes;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonServerTests;

/// <summary>
/// Verifies the idle-timer re-arm vs. shutdown gate:
///  - When the idle timer fires while a request is in flight, the daemon re-arms and keeps serving.
///  - When the idle timer fires with nothing in flight, the daemon shuts down.
///
/// Uses injectable seams (SetIdleTimeoutForTests, SetSuppressEnvironmentExitForTests) so tests
/// run with a deterministic short timeout rather than the production 30-minute idle window.
/// The tests exercise DaemonServer.RunAsync directly at the accept-loop level.
/// </summary>
public sealed class IdleTimer
{
    // Short enough to fire quickly in tests; long enough for pipe I/O, MSBuildWorkspace startup,
    // and task scheduling. MSBuildWorkspace.Create() is synchronous and can take 200–500ms.
    private static readonly TimeSpan ShortIdleTimeout = TimeSpan.FromMilliseconds(400);

    // Hard deadline for each test so a bug in the re-arm logic does not hang CI.
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task WhenIdleFiresWithRequestInFlight_DaemonReArmsAndKeepsServing()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);

        DaemonServer.SetIdleTimeoutForTests(ShortIdleTimeout);
        DaemonServer.SetSuppressEnvironmentExitForTests(true);

        using CancellationTokenSource externalCts = new(TestDeadline);

        try
        {
            // Start the daemon server loop in the background.
            Task daemonTask = Task.Run(
                () => DaemonServer.RunAsync(fakeSolutionPath, externalCts.Token),
                CancellationToken.None);

            // Give the accept loop a moment to start listening.
            await Task.Delay(50, externalCts.Token);

            // Connect a client pipe. The server's accept loop increments inFlight after
            // WaitForConnectionAsync returns, before dispatching the connection task.
            // While the server waits for the request envelope, inFlight == 1.
            using NamedPipeClientStream slowClient = new(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await slowClient.ConnectAsync(externalCts.Token);

            // Wait longer than the idle timeout so the idle timer fires while
            // this connection is still in flight (inFlight == 1).
            await Task.Delay(ShortIdleTimeout + TimeSpan.FromMilliseconds(150), externalCts.Token);

            // Now send the request envelope so the connection completes.
            await PipeProtocol.WriteRequestAsync(
                slowClient,
                PipeProtocol.ProtocolVersion,
                PipeProtocol.ClientInformationalVersion,
                ["list-projects", fakeSolutionPath],
                externalCts.Token);

            // Read the response so the connection closes cleanly (Loading or similar).
            await PipeProtocol.ReadResponseEnvelopeAsync(slowClient, externalCts.Token);

            // Give the daemon a moment to decrement inFlight to 0.
            await Task.Delay(50, externalCts.Token);

            // Act — connect a second client. The daemon must still be running
            // (it re-armed when the idle fired with inFlight > 0).
            using NamedPipeClientStream secondClient = new(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using CancellationTokenSource connectCts = new(TimeSpan.FromSeconds(3));
            await secondClient.ConnectAsync(connectCts.Token);

            await PipeProtocol.WriteRequestAsync(
                secondClient,
                PipeProtocol.ProtocolVersion,
                PipeProtocol.ClientInformationalVersion,
                ["list-projects", fakeSolutionPath],
                externalCts.Token);

            DaemonResponse response = await PipeProtocol.ReadResponseEnvelopeAsync(secondClient, externalCts.Token);

            // Assert — daemon responded (it re-armed and kept serving).
            // Loading or Result are both valid; what matters is the daemon did not exit.
            response.Status.ShouldBeOneOf(
                ResponseStatus.Loading,
                ResponseStatus.Result,
                ResponseStatus.Reloading);

            // Cleanup — cancel the external token to stop the daemon.
            await externalCts.CancelAsync();
            await daemonTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            DaemonServer.SetIdleTimeoutForTests(null);
            DaemonServer.SetSuppressEnvironmentExitForTests(false);
        }
    }

    [Fact]
    public async Task WhenIdleFiresWithNothingInFlight_DaemonExits()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.sln");

        DaemonServer.SetIdleTimeoutForTests(ShortIdleTimeout);
        DaemonServer.SetSuppressEnvironmentExitForTests(true);

        using CancellationTokenSource externalCts = new(TestDeadline);

        try
        {
            // Act — start the daemon with no connections. The idle timer fires and the loop exits.
            Task daemonTask = Task.Run(
                () => DaemonServer.RunAsync(fakeSolutionPath, externalCts.Token),
                CancellationToken.None);

            // Wait up to 5 seconds for the daemon to exit. This covers MSBuildWorkspace
            // startup time plus the idle timeout itself.
            bool exitedBeforeDeadline;
            try
            {
                await daemonTask.WaitAsync(TimeSpan.FromSeconds(5));
                exitedBeforeDeadline = true;
            }
            catch (TimeoutException)
            {
                exitedBeforeDeadline = false;
                await externalCts.CancelAsync();
                // Drain the task so it does not outlive this test.
                await daemonTask.WaitAsync(TimeSpan.FromSeconds(5));
            }

            // Assert — daemon exited naturally after idle timeout with no in-flight requests.
            exitedBeforeDeadline.ShouldBeTrue(
                "daemon should exit after idle timeout with no in-flight requests");
        }
        finally
        {
            DaemonServer.SetIdleTimeoutForTests(null);
            DaemonServer.SetSuppressEnvironmentExitForTests(false);
        }
    }
}
