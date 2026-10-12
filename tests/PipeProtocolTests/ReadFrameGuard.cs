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
            async () => await PipeProtocol.ReadFrameAsync(client, CancellationToken.None));
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
            async () => await PipeProtocol.ReadFrameAsync(client, CancellationToken.None));
    }

    [Fact]
    public async Task WhenLengthIsExactlyMaxFrameBytes_AcceptsFrame()
    {
        // Arrange
        int maxFrameBytes = PipeProtocol.MaxFrameBytes;
        byte[] payload = new byte[maxFrameBytes];
        using MemoryStream stream = new();
        byte[] lenBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBytes, maxFrameBytes);
        await stream.WriteAsync(lenBytes);
        await stream.WriteAsync(payload);
        stream.Position = 0;

        // Act
        byte[] result = await PipeProtocol.ReadFrameAsync(stream, CancellationToken.None);

        // Assert
        result.Length.ShouldBe(maxFrameBytes);
    }

    [Fact]
    public async Task WhenLengthIsBelowMax_RoundTripsCorrectly()
    {
        // Arrange
        byte[] payload = [1, 2, 3, 4, 5];
        using MemoryStream stream = new();
        byte[] lenBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBytes, payload.Length);
        await stream.WriteAsync(lenBytes);
        await stream.WriteAsync(payload);
        stream.Position = 0;

        // Act
        byte[] result = await PipeProtocol.ReadFrameAsync(stream, CancellationToken.None);

        // Assert
        result.ShouldBe(payload);
    }
}
