using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.LimitedWriterTests;

public sealed class WriteLine
{
    [Fact]
    public async Task WhenUnderLimit_WritesAllLines()
    {
        // Arrange
        StringWriter inner = new();
        LimitedWriter writer = new(inner, maxLines: 5);

        // Act
        await writer.WriteLineAsync("line 1");
        await writer.WriteLineAsync("line 2");
        await writer.WriteLineAsync("line 3");

        // Assert
        writer.Suppressed.ShouldBe(0);
        inner.ToString().ShouldBe("line 1\r\nline 2\r\nline 3\r\n");
    }

    [Fact]
    public async Task WhenOverLimit_SuppressesExcessLines()
    {
        // Arrange
        StringWriter inner = new();
        LimitedWriter writer = new(inner, maxLines: 2);

        // Act
        await writer.WriteLineAsync("line 1");
        await writer.WriteLineAsync("line 2");
        await writer.WriteLineAsync("line 3");
        await writer.WriteLineAsync("line 4");

        // Assert
        writer.Suppressed.ShouldBe(2);
        inner.ToString().ShouldBe("line 1\r\nline 2\r\n");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task WhenMaxLinesIsZeroOrNegative_PassesThrough(int maxLines)
    {
        // Arrange
        StringWriter inner = new();
        LimitedWriter writer = new(inner, maxLines);

        // Act
        await writer.WriteLineAsync("line 1");
        await writer.WriteLineAsync("line 2");
        await writer.WriteLineAsync("line 3");

        // Assert
        writer.Suppressed.ShouldBe(0);
        inner.ToString().ShouldBe("line 1\r\nline 2\r\nline 3\r\n");
    }
}
