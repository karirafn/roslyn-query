using System.IO.Pipes;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonServerTests;

/// <summary>
/// Verifies that the daemon's accept loop can serve multiple concurrent connections.
/// </summary>
public sealed class ConcurrentAccept
{
    [Fact]
    public async Task WhenTwoClientsConnectConcurrently_BothAreServedSuccessfully()
    {
        // Arrange — server with MaxAllowedServerInstances > 1
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);

        using CancellationTokenSource serverCts = new(TimeSpan.FromSeconds(15));
        int connectionsHandled = 0;

        // Simulate a concurrent-accept server with MaxAllowedServerInstances
        const int MaxInstances = 10;

        Task serverTask = Task.Run(
            async () =>
            {
                List<Task> connectionTasks = [];

                // Accept loop — accepts up to MaxInstances connections
                for (int i = 0; i < 2; i++)
                {
#pragma warning disable CA2000 // Disposed inside the connection task
                    NamedPipeServerStream pipe = new(
                        pipeName,
                        PipeDirection.InOut,
                        MaxInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
#pragma warning restore CA2000

                    await pipe.WaitForConnectionAsync(serverCts.Token);

                    // Capture snapshot and dispatch to a task — mirrors the real accept loop
                    connectionTasks.Add(Task.Run(async () =>
                    {
                        await using (pipe.ConfigureAwait(false))
                        {
                            _ = await PipeProtocol.ReadRequestEnvelopeAsync(pipe, serverCts.Token);
                            // Simulate a slow command (100ms) so the two connections overlap
                            await Task.Delay(100, serverCts.Token);
                            await PipeProtocol.WriteResultAsync(pipe, "done", "", 0, serverCts.Token);
                            Interlocked.Increment(ref connectionsHandled);
                        }
                    }, serverCts.Token));
                }

                await Task.WhenAll(connectionTasks);
            },
            serverCts.Token);

        // Act — launch two clients concurrently
        // StringWriter does not hold unmanaged resources; no using needed here.
        // Both tasks are awaited before the method exits, satisfying lifetime requirements.
#pragma warning disable CA2025 // Tasks complete before method exits — StringWriter has no unmanaged resources
        StringWriter stdout1 = new();
        StringWriter stderr1 = new();
        StringWriter stdout2 = new();
        StringWriter stderr2 = new();

        Task<DaemonAttempt> client1 = DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["find-refs", "A"],
            stdout1,
            stderr1);

        Task<DaemonAttempt> client2 = DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["find-refs", "B"],
            stdout2,
            stderr2);
#pragma warning restore CA2025

        DaemonAttempt[] results = await Task.WhenAll(client1, client2);
        await serverTask;

        // Assert — both clients got a Result
        results[0].Outcome.ShouldBe(DaemonOutcome.Result);
        results[0].ExitCode.ShouldBe(0);
        results[1].Outcome.ShouldBe(DaemonOutcome.Result);
        results[1].ExitCode.ShouldBe(0);
        connectionsHandled.ShouldBe(2);
    }
}
