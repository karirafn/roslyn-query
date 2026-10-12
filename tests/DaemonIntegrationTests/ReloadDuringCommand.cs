using System.IO.Pipes;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonIntegrationTests;

public sealed class ReloadDuringCommand
{
    [Fact]
    public async Task WhenServerWritesReloadingStatus_ClientReturnsOutcomeReloading()
    {
        // Arrange — server replies with a Reloading status-only byte
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        Task serverTask = Task.Run(
            async () =>
            {
                using NamedPipeServerStream pipe = new(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cts.Token);
                _ = await PipeProtocol.ReadRequestEnvelopeAsync(pipe, cts.Token);
                await PipeProtocol.WriteStatusAsync(
                    pipe,
                    ResponseStatus.Reloading,
                    cts.Token);
            },
            cts.Token);

        StringWriter stdout = new();
        StringWriter stderr = new();

        // Act
        DaemonAttempt attempt = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["find-callers", "--symbol", "Foo.Bar"],
            stdout,
            stderr);

        await serverTask;

        // Assert
        attempt.Outcome.ShouldBe(DaemonOutcome.Reloading);
    }

    [Fact]
    public async Task WhenServerWritesLoadingStatus_ClientReturnsOutcomeLoading()
    {
        // Arrange — server replies with a Loading status-only byte
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        Task serverTask = Task.Run(
            async () =>
            {
                using NamedPipeServerStream pipe = new(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cts.Token);
                _ = await PipeProtocol.ReadRequestEnvelopeAsync(pipe, cts.Token);
                await PipeProtocol.WriteStatusAsync(
                    pipe,
                    ResponseStatus.Loading,
                    cts.Token);
            },
            cts.Token);

        StringWriter stdout = new();
        StringWriter stderr = new();

        // Act
        DaemonAttempt attempt = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["find-callers", "--symbol", "Foo.Bar"],
            stdout,
            stderr);

        await serverTask;

        // Assert
        attempt.Outcome.ShouldBe(DaemonOutcome.Loading);
    }
}
