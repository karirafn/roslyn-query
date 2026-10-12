using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.PipeProtocolTests;

public sealed class ResponseEnvelope
{
    // ── Result status (behavior c) ──────────────────────────────────────────

    [Fact]
    public async Task WhenResultRoundTripped_StatusIsResult()
    {
        // Arrange
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteResultAsync(stream, "output", "warning", 0);
        stream.Position = 0;
        DaemonResponse response = await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        response.Status.ShouldBe(ResponseStatus.Result);
    }

    [Fact]
    public async Task WhenResultRoundTripped_StdoutMatches()
    {
        // Arrange
        const string Stdout = "line1\nline2\nline3";
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteResultAsync(stream, Stdout, "", 0);
        stream.Position = 0;
        DaemonResponse response = await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        response.Stdout.ShouldBe(Stdout);
    }

    [Fact]
    public async Task WhenResultRoundTripped_StderrMatches()
    {
        // Arrange
        const string Stderr = "warning: something\nwarning: another";
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteResultAsync(stream, "", Stderr, 0);
        stream.Position = 0;
        DaemonResponse response = await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        response.Stderr.ShouldBe(Stderr);
    }

    [Fact]
    public async Task WhenResultRoundTripped_ExitCodeMatches()
    {
        // Arrange
        const int ExitCode = 1;
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteResultAsync(stream, "", "", ExitCode);
        stream.Position = 0;
        DaemonResponse response = await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        response.ExitCode.ShouldBe(ExitCode);
    }

    [Fact]
    public async Task WhenResultRoundTripped_ErrorMessageIsEmpty()
    {
        // Arrange
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteResultAsync(stream, "output", "warn", 0);
        stream.Position = 0;
        DaemonResponse response = await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        response.ErrorMessage.ShouldBeEmpty();
    }

    [Fact]
    public async Task WhenResultRoundTripped_NonZeroExitCodeRoundTripsCorrectly()
    {
        // Arrange
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteResultAsync(stream, "output", "error info", 75);
        stream.Position = 0;
        DaemonResponse response = await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        response.ShouldSatisfyAllConditions(
            () => response.Status.ShouldBe(ResponseStatus.Result),
            () => response.ExitCode.ShouldBe(75),
            () => response.Stdout.ShouldBe("output"),
            () => response.Stderr.ShouldBe("error info"));
    }

    // ── Loading status (behavior d) ─────────────────────────────────────────

    [Fact]
    public async Task WhenLoadingStatusWritten_StatusIsLoading()
    {
        // Arrange
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteStatusAsync(stream, ResponseStatus.Loading);
        stream.Position = 0;
        DaemonResponse response = await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        response.Status.ShouldBe(ResponseStatus.Loading);
    }

    [Fact]
    public async Task WhenLoadingStatusWritten_BodyIsEmpty()
    {
        // Arrange
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteStatusAsync(stream, ResponseStatus.Loading);
        stream.Position = 0;
        DaemonResponse response = await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        response.ShouldSatisfyAllConditions(
            () => response.Stdout.ShouldBeEmpty(),
            () => response.Stderr.ShouldBeEmpty(),
            () => response.ErrorMessage.ShouldBeEmpty(),
            () => response.ExitCode.ShouldBe(0));
    }

    // ── Reloading status (behavior d) ───────────────────────────────────────

    [Fact]
    public async Task WhenReloadingStatusWritten_StatusIsReloading()
    {
        // Arrange
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteStatusAsync(stream, ResponseStatus.Reloading);
        stream.Position = 0;
        DaemonResponse response = await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        response.Status.ShouldBe(ResponseStatus.Reloading);
    }

    [Fact]
    public async Task WhenReloadingStatusWritten_BodyIsEmpty()
    {
        // Arrange
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteStatusAsync(stream, ResponseStatus.Reloading);
        stream.Position = 0;
        DaemonResponse response = await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        response.ShouldSatisfyAllConditions(
            () => response.Stdout.ShouldBeEmpty(),
            () => response.Stderr.ShouldBeEmpty(),
            () => response.ErrorMessage.ShouldBeEmpty(),
            () => response.ExitCode.ShouldBe(0));
    }

    // ── VersionMismatch status (behavior d) ─────────────────────────────────

    [Fact]
    public async Task WhenVersionMismatchStatusWritten_StatusIsVersionMismatch()
    {
        // Arrange
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteStatusAsync(stream, ResponseStatus.VersionMismatch);
        stream.Position = 0;
        DaemonResponse response = await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        response.Status.ShouldBe(ResponseStatus.VersionMismatch);
    }

    [Fact]
    public async Task WhenVersionMismatchStatusWritten_BodyIsEmpty()
    {
        // Arrange
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteStatusAsync(stream, ResponseStatus.VersionMismatch);
        stream.Position = 0;
        DaemonResponse response = await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        response.ShouldSatisfyAllConditions(
            () => response.Stdout.ShouldBeEmpty(),
            () => response.Stderr.ShouldBeEmpty(),
            () => response.ErrorMessage.ShouldBeEmpty(),
            () => response.ExitCode.ShouldBe(0));
    }

    // ── Error status (behavior e) ────────────────────────────────────────────

    [Fact]
    public async Task WhenErrorWritten_StatusIsError()
    {
        // Arrange
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteErrorAsync(stream, "Something went wrong");
        stream.Position = 0;
        DaemonResponse response = await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        response.Status.ShouldBe(ResponseStatus.Error);
    }

    [Fact]
    public async Task WhenErrorWritten_ErrorMessageMatches()
    {
        // Arrange
        const string Message = "NullReferenceException: object reference not set";
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteErrorAsync(stream, Message);
        stream.Position = 0;
        DaemonResponse response = await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        response.ErrorMessage.ShouldBe(Message);
    }

    [Fact]
    public async Task WhenErrorWritten_ExitCodeIsOne()
    {
        // Arrange
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteErrorAsync(stream, "command failed");
        stream.Position = 0;
        DaemonResponse response = await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        response.ExitCode.ShouldBe(1);
    }

    [Fact]
    public async Task WhenErrorWritten_StdoutAndStderrAreEmpty()
    {
        // Arrange
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteErrorAsync(stream, "command failed");
        stream.Position = 0;
        DaemonResponse response = await PipeProtocol.ReadResponseEnvelopeAsync(stream, CancellationToken.None);

        // Assert
        response.ShouldSatisfyAllConditions(
            () => response.Stdout.ShouldBeEmpty(),
            () => response.Stderr.ShouldBeEmpty());
    }

    // ── WriteStatusAsync guard (A4) ──────────────────────────────────────────

    [Theory]
    [InlineData(ResponseStatus.Result)]
    [InlineData(ResponseStatus.Error)]
    public async Task WhenWriteStatusCalledWithBodyStatus_ThrowsArgumentOutOfRangeException(
        ResponseStatus status)
    {
        // Arrange
        using MemoryStream stream = new();

        // Act & Assert
        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await PipeProtocol.WriteStatusAsync(stream, status));
    }

    [Theory]
    [InlineData(ResponseStatus.Loading)]
    [InlineData(ResponseStatus.Reloading)]
    [InlineData(ResponseStatus.VersionMismatch)]
    public async Task WhenWriteStatusCalledWithStatusOnlyStatus_Succeeds(ResponseStatus status)
    {
        // Arrange
        using MemoryStream stream = new();

        // Act
        await PipeProtocol.WriteStatusAsync(stream, status);

        // Assert
        stream.Length.ShouldBe(1);
    }
}
