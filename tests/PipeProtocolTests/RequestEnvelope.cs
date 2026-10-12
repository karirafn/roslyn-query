using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.PipeProtocolTests;

public sealed class RequestEnvelope
{
    [Fact]
    public async Task WhenRoundTripped_ProtocolVersionMatches()
    {
        // Arrange
        string[] args = ["find-refs", "MyClass"];
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteRequestAsync(stream, PipeProtocol.ProtocolVersion, "1.2.3", args);
        stream.Position = 0;
        (int protocolVersion, string _, string[] _) =
            await PipeProtocol.ReadRequestEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        protocolVersion.ShouldBe(PipeProtocol.ProtocolVersion);
    }

    [Fact]
    public async Task WhenRoundTripped_ClientVersionMatches()
    {
        // Arrange
        string[] args = ["find-refs", "MyClass"];
        const string ClientVersion = "1.2.3-preview.4";
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteRequestAsync(stream, PipeProtocol.ProtocolVersion, ClientVersion, args);
        stream.Position = 0;
        (int _, string clientVersion, string[] _) =
            await PipeProtocol.ReadRequestEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        clientVersion.ShouldBe(ClientVersion);
    }

    [Fact]
    public async Task WhenRoundTripped_ArgsMatch()
    {
        // Arrange
        string[] args = ["find-callers", "--symbol", "MyClass.MyMethod", "--quiet"];
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteRequestAsync(stream, PipeProtocol.ProtocolVersion, "1.0.0", args);
        stream.Position = 0;
        (int _, string _, string[] resultArgs) =
            await PipeProtocol.ReadRequestEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        resultArgs.ShouldBe(args);
    }

    [Fact]
    public async Task WhenProtocolVersionIsCustom_RoundTripsCorrectly()
    {
        // Arrange
        string[] args = ["list-projects"];
        const int CustomVersion = 99;
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteRequestAsync(stream, CustomVersion, "2.0.0", args);
        stream.Position = 0;
        (int protocolVersion, string _, string[] _) =
            await PipeProtocol.ReadRequestEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        protocolVersion.ShouldBe(CustomVersion);
    }

    [Fact]
    public async Task WhenArgsAreEmpty_RoundTripsCorrectly()
    {
        // Arrange
        string[] args = [];
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteRequestAsync(stream, PipeProtocol.ProtocolVersion, "1.0.0", args);
        stream.Position = 0;
        (int _, string _, string[] resultArgs) =
            await PipeProtocol.ReadRequestEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        resultArgs.ShouldBeEmpty();
    }

    [Fact]
    public async Task WhenArgsContainShutdownCommand_RoundTripsCorrectly()
    {
        // Arrange — the shutdown sentinel rides inside the args JSON exactly as today
        string[] args = [PipeProtocol.ShutdownCommand];
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteRequestAsync(stream, PipeProtocol.ProtocolVersion, "1.0.0", args);
        stream.Position = 0;
        (int _, string _, string[] resultArgs) =
            await PipeProtocol.ReadRequestEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        resultArgs.ShouldBe(args);
    }

    [Fact]
    public async Task ProtocolVersionInt_IsWrittenAsLeadingRawBigEndianInt32_NotLengthPrefixed()
    {
        // Arrange — verify the wire format: protocol-version is a raw 4-byte big-endian int32,
        // not a length-prefixed frame. The next 4 bytes must NOT be interpreted as content.
        using MemoryStream stream = new();
        await PipeProtocol.WriteRequestAsync(stream, 1, "v", ["a"]);
        stream.Position = 0;

        // Act — read the first 4 bytes as a raw big-endian int32
        byte[] first4 = new byte[4];
        int bytesRead = await stream.ReadAsync(first4);

        // Assert
        bytesRead.ShouldBe(4);
        int versionFromWire = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(first4);
        versionFromWire.ShouldBe(1); // the raw version int, not a length prefix
    }
}
