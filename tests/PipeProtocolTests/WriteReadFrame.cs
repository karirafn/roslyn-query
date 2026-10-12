using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.PipeProtocolTests;

public sealed class WriteReadFrame
{
    [Fact]
    public async Task RequestEnvelopeRoundTrip_ArgsMatch()
    {
        // Arrange
        string[] args = ["find-callers", "--symbol", "MyClass.MyMethod"];
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteRequestAsync(stream, PipeProtocol.ProtocolVersion, "1.0.0", args);
        stream.Position = 0;
        (int _, string _, string[] result) =
            await PipeProtocol.ReadRequestEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        result.ShouldBe(args);
    }

    [Fact]
    public async Task ResultResponseRoundTrip_AllFieldsMatch()
    {
        // Arrange
        const string Stdout = "line1\nline2";
        const string Stderr = "warning: something";
        const int ExitCode = 0;
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteResultAsync(stream, Stdout, Stderr, ExitCode);
        stream.Position = 0;
        DaemonResponse response =
            await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        response.ShouldSatisfyAllConditions(
            () => response.Stdout.ShouldBe(Stdout),
            () => response.Stderr.ShouldBe(Stderr),
            () => response.ExitCode.ShouldBe(ExitCode),
            () => response.Status.ShouldBe(ResponseStatus.Result));
    }

    [Fact]
    public async Task ResultResponseRoundTrip_EmptyStdoutAndStderr()
    {
        // Arrange
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteResultAsync(stream, "", "", 0);
        stream.Position = 0;
        DaemonResponse response =
            await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        response.ShouldSatisfyAllConditions(
            () => response.Stdout.ShouldBeEmpty(),
            () => response.Stderr.ShouldBeEmpty(),
            () => response.ExitCode.ShouldBe(0));
    }

    [Fact]
    public async Task ResultResponseRoundTrip_NonZeroExitCode()
    {
        // Arrange
        const string Stdout = "some output";
        const string Stderr = "error: failed";
        const int ExitCode = 75;
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteResultAsync(stream, Stdout, Stderr, ExitCode);
        stream.Position = 0;
        DaemonResponse response =
            await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        response.ShouldSatisfyAllConditions(
            () => response.Stdout.ShouldBe(Stdout),
            () => response.Stderr.ShouldBe(Stderr),
            () => response.ExitCode.ShouldBe(ExitCode));
    }
}
