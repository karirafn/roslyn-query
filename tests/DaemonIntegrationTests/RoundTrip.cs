using System.IO.Pipes;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonIntegrationTests;

public sealed class RoundTrip
{
    [Fact]
    public async Task WhenServerWritesResult_ClientReceivesResponse()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);
        string[] sentArgs = ["find-callers", "--symbol", "MyClass.MyMethod"];
        string expectedStdout = "find-callers --symbol MyClass.MyMethod";

        Task serverTask = Task.Run(async () =>
        {
            using NamedPipeServerStream pipe = new(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await pipe.WaitForConnectionAsync();
            (_, _, string[] args) = await PipeProtocol.ReadRequestEnvelopeAsync(pipe, CancellationToken.None);
            await PipeProtocol.WriteResultAsync(
                pipe,
                string.Join(" ", args),
                "",
                0);
        });

        StringWriter stdout = new();
        StringWriter stderr = new();

        // Act
        DaemonAttempt attempt = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            sentArgs,
            stdout,
            stderr);

        await serverTask;

        // Assert
        attempt.Outcome.ShouldBe(DaemonOutcome.Result);
        attempt.ExitCode.ShouldBe(0);
        stdout.ToString().ShouldBe(expectedStdout);
        stderr.ToString().ShouldBe("");
    }

    [Fact]
    public async Task WhenServerWritesNonZeroExit_ClientReceivesExitAndStderr()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);
        string expectedStderr = "error: something went wrong";
        int expectedExitCode = 1;

        Task serverTask = Task.Run(async () =>
        {
            using NamedPipeServerStream pipe = new(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await pipe.WaitForConnectionAsync();
            _ = await PipeProtocol.ReadRequestEnvelopeAsync(pipe, CancellationToken.None);
            await PipeProtocol.WriteResultAsync(
                pipe,
                "",
                expectedStderr,
                expectedExitCode);
        });

        StringWriter stdout = new();
        StringWriter stderr = new();

        // Act
        DaemonAttempt attempt = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["some-command"],
            stdout,
            stderr);

        await serverTask;

        // Assert
        attempt.Outcome.ShouldBe(DaemonOutcome.Result);
        attempt.ExitCode.ShouldBe(expectedExitCode);
        stdout.ToString().ShouldBe("");
        stderr.ToString().ShouldBe(expectedStderr);
    }

    [Fact]
    public async Task WhenNoDaemonRunning_ReturnsOutcomeNotRunning()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        StringWriter stdout = new();
        StringWriter stderr = new();

        // Act
        DaemonAttempt attempt = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["some-command"],
            stdout,
            stderr);

        // Assert
        attempt.Outcome.ShouldBe(DaemonOutcome.NotRunning);
    }
}
