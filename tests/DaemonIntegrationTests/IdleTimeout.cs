using Shouldly;

namespace roslyn_query.Tests.DaemonIntegrationTests;

public sealed class IdleTimeout
{
    [Fact]
    public async Task CancellationTokenSource_CancelsAfterTimeout()
    {
        // Arrange
        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(200));

        // Act
        await Task.Delay(500);

        // Assert
        cts.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task LinkedTokenSource_CancelsWhenIdleTokenFires()
    {
        // Arrange
        using CancellationTokenSource externalCts = new();
        using CancellationTokenSource idleCts = new(TimeSpan.FromMilliseconds(200));
        using CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(externalCts.Token, idleCts.Token);

        // Act
        await Task.Delay(500);

        // Assert
        linkedCts.Token.IsCancellationRequested.ShouldBeTrue();
        externalCts.Token.IsCancellationRequested.ShouldBeFalse();
    }
}
