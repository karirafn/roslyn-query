using System.IO.Pipes;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonIntegrationTests;

public sealed class RoundTrip
{
    [Fact]
    public async Task ClientReceivesServerResponse()
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
            string[] args = await PipeProtocol.ReadRequestAsync(pipe, CancellationToken.None);
            await PipeProtocol.WriteResponseAsync(
                pipe,
                string.Join(" ", args),
                "",
                0);
        });

        StringWriter stdout = new();
        StringWriter stderr = new();

        // Act
        int? exitCode = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            sentArgs,
            stdout,
            stderr);

        await serverTask;

        // Assert
        exitCode.ShouldBe(0);
        stdout.ToString().ShouldBe(expectedStdout);
        stderr.ToString().ShouldBe("");
    }

    [Fact]
    public async Task ClientReceivesNonZeroExitCodeAndStderr()
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
            await PipeProtocol.ReadRequestAsync(pipe, CancellationToken.None);
            await PipeProtocol.WriteResponseAsync(
                pipe,
                "",
                expectedStderr,
                expectedExitCode);
        });

        StringWriter stdout = new();
        StringWriter stderr = new();

        // Act
        int? exitCode = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["some-command"],
            stdout,
            stderr);

        await serverTask;

        // Assert
        exitCode.ShouldBe(expectedExitCode);
        stdout.ToString().ShouldBe("");
        stderr.ToString().ShouldBe(expectedStderr);
    }

    [Fact]
    public async Task WhenNoDaemonRunning_ReturnsNull()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        StringWriter stdout = new();
        StringWriter stderr = new();

        // Act
        int? exitCode = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["some-command"],
            stdout,
            stderr);

        // Assert
        exitCode.ShouldBeNull();
    }
}
