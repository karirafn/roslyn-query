using System.IO.Pipes;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonServerTests;

/// <summary>
/// Verifies that a command throwing a non-cancellation exception produces an Error reply
/// (not a dropped connection) and the daemon continues serving subsequent connections.
/// </summary>
public sealed class ExceptionReply
{
    [Fact]
    public async Task WhenCommandThrows_DaemonRepliesError_AndContinuesServing()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);
        string exceptionMessage = "command failed unexpectedly";

        using CancellationTokenSource serverCts = new(TimeSpan.FromSeconds(10));
        int connectionsHandled = 0;

        // Minimal server that simulates a throwing command handler
        Task serverTask = Task.Run(
            async () =>
            {
                // Handle two connections
                for (int i = 0; i < 2; i++)
                {
                    using NamedPipeServerStream pipe = new(
                        pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await pipe.WaitForConnectionAsync(serverCts.Token);
                    _ = await PipeProtocol.ReadRequestEnvelopeAsync(pipe, serverCts.Token);

                    if (i == 0)
                    {
                        // First connection: simulate a command that throws
                        await PipeProtocol.WriteErrorAsync(pipe, exceptionMessage, serverCts.Token);
                    }
                    else
                    {
                        // Second connection: normal result
                        await PipeProtocol.WriteResultAsync(pipe, "ok", "", 0, serverCts.Token);
                    }

                    connectionsHandled++;
                    pipe.Disconnect();
                }
            },
            serverCts.Token);

        StringWriter stdout1 = new();
        StringWriter stderr1 = new();

        // Act — first client gets the error reply
        DaemonAttempt attempt1 = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["find-refs", "Foo"],
            stdout1,
            stderr1);

        StringWriter stdout2 = new();
        StringWriter stderr2 = new();

        // Act — second client gets normal result (daemon is still running)
        DaemonAttempt attempt2 = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["find-refs", "Bar"],
            stdout2,
            stderr2);

        await serverTask;

        // Assert — first attempt is Result exit 1 with error message in stderr; no retry
        attempt1.Outcome.ShouldBe(DaemonOutcome.Result);
        attempt1.ExitCode.ShouldBe(1);
        stderr1.ToString().ShouldContain(exceptionMessage);

        // Assert — second attempt succeeds (daemon survived the error)
        attempt2.Outcome.ShouldBe(DaemonOutcome.Result);
        attempt2.ExitCode.ShouldBe(0);
        stdout2.ToString().ShouldBe("ok");

        connectionsHandled.ShouldBe(2);
    }
}
