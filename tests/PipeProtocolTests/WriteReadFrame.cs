using System.IO.Pipes;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.PipeProtocolTests;

public sealed class WriteReadFrame
{
    [Fact]
    public async Task RequestRoundTrip_ArgsMatch()
    {
        // Arrange
        string[] args = ["find-callers", "--symbol", "MyClass.MyMethod"];
        using AnonymousPipeServerStream server = new(PipeDirection.Out);
        using AnonymousPipeClientStream client = new(PipeDirection.In, server.ClientSafePipeHandle);

        // Act
        await PipeProtocol.WriteRequestAsync(server, args);
        server.Close();
        string[] result = await PipeProtocol.ReadRequestAsync(client, CancellationToken.None);

        // Assert
        result.ShouldBe(args);
    }

    [Fact]
    public async Task ResponseRoundTrip_AllFieldsMatch()
    {
        // Arrange
        string stdout = "line1\nline2";
        string stderr = "warning: something";
        int exitCode = 0;
        using AnonymousPipeServerStream server = new(PipeDirection.Out);
        using AnonymousPipeClientStream client = new(PipeDirection.In, server.ClientSafePipeHandle);

        // Act
        await PipeProtocol.WriteResponseAsync(server, stdout, stderr, exitCode);
        server.Close();
        (string resultStdout, string resultStderr, int resultExitCode) =
            await PipeProtocol.ReadResponseAsync(client, CancellationToken.None);

        // Assert
        resultStdout.ShouldBe(stdout);
        resultStderr.ShouldBe(stderr);
        resultExitCode.ShouldBe(exitCode);
    }

    [Fact]
    public async Task ResponseRoundTrip_EmptyStdoutAndStderr()
    {
        // Arrange
        string stdout = "";
        string stderr = "";
        int exitCode = 0;
        using AnonymousPipeServerStream server = new(PipeDirection.Out);
        using AnonymousPipeClientStream client = new(PipeDirection.In, server.ClientSafePipeHandle);

        // Act
        await PipeProtocol.WriteResponseAsync(server, stdout, stderr, exitCode);
        server.Close();
        (string resultStdout, string resultStderr, int resultExitCode) =
            await PipeProtocol.ReadResponseAsync(client, CancellationToken.None);

        // Assert
        resultStdout.ShouldBe(stdout);
        resultStderr.ShouldBe(stderr);
        resultExitCode.ShouldBe(exitCode);
    }

    [Fact]
    public async Task ResponseRoundTrip_NonZeroExitCode()
    {
        // Arrange
        string stdout = "some output";
        string stderr = "error: failed";
        int exitCode = 75;
        using AnonymousPipeServerStream server = new(PipeDirection.Out);
        using AnonymousPipeClientStream client = new(PipeDirection.In, server.ClientSafePipeHandle);

        // Act
        await PipeProtocol.WriteResponseAsync(server, stdout, stderr, exitCode);
        server.Close();
        (string resultStdout, string resultStderr, int resultExitCode) =
            await PipeProtocol.ReadResponseAsync(client, CancellationToken.None);

        // Assert
        resultStdout.ShouldBe(stdout);
        resultStderr.ShouldBe(stderr);
        resultExitCode.ShouldBe(exitCode);
    }
}
