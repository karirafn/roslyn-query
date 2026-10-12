using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.PipeProtocolTests;

public sealed class ResponseStatusGuard
{
    [Fact]
    public void ResponseStatus_ZeroByte_IsNotDefinedInEnum()
    {
        // Arrange
        byte zeroByte = 0;

        // Act
        bool isDefined = Enum.IsDefined(typeof(ResponseStatus), zeroByte);

        // Assert
        isDefined.ShouldBeFalse();
    }

    [Fact]
    public void ResponseStatus_ValuesSixAndAbove_AreNotDefined()
    {
        // Arrange
        byte[] invalidValues = [6, 7, 100, 255];

        // Act & Assert
        foreach (byte value in invalidValues)
        {
            Enum.IsDefined(typeof(ResponseStatus), value).ShouldBeFalse();
        }
    }

    [Fact]
    public void ResponseStatus_ValuesOneToFive_AreAllDefined()
    {
        // Arrange
        byte[] validValues = [1, 2, 3, 4, 5];

        // Act & Assert
        foreach (byte value in validValues)
        {
            Enum.IsDefined(typeof(ResponseStatus), value).ShouldBeTrue();
        }
    }

    [Fact]
    public void ProtocolVersion_IsOne()
    {
        // Arrange & Act
        int version = PipeProtocol.ProtocolVersion;

        // Assert
        version.ShouldBe(1);
    }

    [Fact]
    public void ClientInformationalVersion_IsNotEmpty()
    {
        // Arrange & Act
        string version = PipeProtocol.ClientInformationalVersion;

        // Assert
        version.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void ClientInformationalVersion_DoesNotContainPlusSign()
    {
        // Arrange & Act
        string version = PipeProtocol.ClientInformationalVersion;

        // Assert
        // SourceLink appends +<sha>; the property strips it at the first '+'
        version.ShouldNotContain("+");
    }

    [Fact]
    public async Task ReadResponseEnvelope_WhenStatusByteIsZero_ThrowsInvalidDataException()
    {
        // Arrange
        using MemoryStream stream = new();
        stream.WriteByte(0); // zero is reserved/invalid
        stream.Position = 0;

        // Act & Assert
        await Should.ThrowAsync<InvalidDataException>(
            async () => await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadResponseEnvelope_WhenStatusByteIsAboveFive_ThrowsInvalidDataException()
    {
        // Arrange
        using MemoryStream stream = new();
        stream.WriteByte(6); // 6 is above the defined range
        stream.Position = 0;

        // Act & Assert
        await Should.ThrowAsync<InvalidDataException>(
            async () => await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task ReadResponseEnvelope_WhenStreamIsEmpty_ThrowsInvalidDataException()
    {
        // Arrange
        using MemoryStream stream = new(); // empty — EOF immediately
        stream.Position = 0;

        // Act & Assert
        // EOF before the status byte → InvalidDataException (old-daemon / short-reply detector)
        await Should.ThrowAsync<InvalidDataException>(
            async () => await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None));
    }
}
