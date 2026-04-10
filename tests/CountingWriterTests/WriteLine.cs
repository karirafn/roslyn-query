using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.CountingWriterTests;

public sealed class WriteLine
{
    [Fact]
    public void WhenLinesWritten_CountsAll()
    {
        // Arrange
        CountingWriter writer = new();

        // Act
        writer.WriteLine("line 1");
        writer.WriteLine("line 2");
        writer.WriteLine("line 3");

        // Assert
        writer.Count.ShouldBe(3);
    }

    [Fact]
    public void WhenHeaderLineWritten_DoesNotCount()
    {
        // Arrange
        CountingWriter writer = new();

        // Act
        writer.WriteLine("# Foo.Bar");

        // Assert
        writer.Count.ShouldBe(0);
    }

    [Fact]
    public void WhenMixedHeaderAndResultLines_CountsOnlyResults()
    {
        // Arrange
        CountingWriter writer = new();

        // Act
        writer.WriteLine("# Foo.Bar");
        writer.WriteLine("src/Foo.cs:10");
        writer.WriteLine("src/Bar.cs:20");

        // Assert
        writer.Count.ShouldBe(2);
    }

    [Fact]
    public void WhenNullValueWritten_CountsIt()
    {
        // Arrange
        CountingWriter writer = new();

        // Act
        writer.WriteLine((string?)null);

        // Assert
        writer.Count.ShouldBe(1);
    }

    [Fact]
    public void WhenNoLinesWritten_CountIsZero()
    {
        // Arrange
        CountingWriter writer = new();

        // Act — nothing

        // Assert
        writer.Count.ShouldBe(0);
    }
}
