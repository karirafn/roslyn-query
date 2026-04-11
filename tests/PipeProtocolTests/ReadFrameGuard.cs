using System.Buffers.Binary;
using System.IO.Pipes;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.PipeProtocolTests;

public sealed class ReadFrameGuard
{
    [Fact]
    public async Task WhenLengthIsMaxInt32_ThrowsInvalidDataException()
    {
        // Arrange
        using AnonymousPipeServerStream server = new(PipeDirection.Out);
        using AnonymousPipeClientStream client = new(PipeDirection.In, server.ClientSafePipeHandle);

        byte[] lenBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBytes, 0x7FFFFFFF);
        await server.WriteAsync(lenBytes);
        server.Close();

        // Act & Assert
        await Should.ThrowAsync<InvalidDataException>(
            async () => await PipeProtocol.ReadRequestAsync(client, CancellationToken.None));
    }

    [Fact]
    public async Task WhenLengthIsNegative_ThrowsInvalidDataException()
    {
        // Arrange
        using AnonymousPipeServerStream server = new(PipeDirection.Out);
        using AnonymousPipeClientStream client = new(PipeDirection.In, server.ClientSafePipeHandle);

        byte[] lenBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBytes, -1);
        await server.WriteAsync(lenBytes);
        server.Close();

        // Act & Assert
        await Should.ThrowAsync<InvalidDataException>(
            async () => await PipeProtocol.ReadRequestAsync(client, CancellationToken.None));
    }

    [Fact]
    public async Task WhenLengthIsExactlyMaxFrameBytes_AcceptsFrame()
    {
        // Arrange
        int maxFrameBytes = 64 * 1024 * 1024;
        string largeStdout = new(' ', maxFrameBytes);
        using MemoryStream stream = new();
        await PipeProtocol.WriteResponseAsync(stream, largeStdout, "", 0);
        stream.Position = 0;

        // Act
        (string stdout, _, int exitCode) =
            await PipeProtocol.ReadResponseAsync(stream, CancellationToken.None);

        // Assert
        stdout.Length.ShouldBe(maxFrameBytes);
        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task WhenLengthIsBelowMax_RoundTripsCorrectly()
    {
        // Arrange
        string[] args = ["describe", "--symbol", "Foo.Bar"];
        using AnonymousPipeServerStream server = new(PipeDirection.Out);
        using AnonymousPipeClientStream client = new(PipeDirection.In, server.ClientSafePipeHandle);

        // Act
        await PipeProtocol.WriteRequestAsync(server, args);
        server.Close();
        string[] result = await PipeProtocol.ReadRequestAsync(client, CancellationToken.None);

        // Assert
        result.ShouldBe(args);
    }
}
