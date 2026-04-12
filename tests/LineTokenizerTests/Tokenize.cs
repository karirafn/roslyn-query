using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.LineTokenizerTests;

public sealed class Tokenize
{
    [Fact]
    public void QuotedArgWithSpaces_ReturnsSingleToken()
    {
        // Arrange
        string line = @"find-refs ""My Class"" solution.sln";

        // Act
        string[] result = LineTokenizer.Tokenize(line);

        // Assert
        result.ShouldBe(["find-refs", "My Class", "solution.sln"]);
    }

    [Fact]
    public void UnquotedArgs_ReturnsSameAsSplit()
    {
        // Arrange
        string line = "find-refs MyClass solution.sln";

        // Act
        string[] result = LineTokenizer.Tokenize(line);

        // Assert
        result.ShouldBe(["find-refs", "MyClass", "solution.sln"]);
    }

    [Fact]
    public void UnterminatedQuote_ConsumesToEndOfLine()
    {
        // Arrange
        string line = @"find-refs ""My Class";

        // Act
        string[] result = LineTokenizer.Tokenize(line);

        // Assert
        result.ShouldBe(["find-refs", "My Class"]);
    }

    [Fact]
    public void EmptyQuotedString_ReturnsEmptyStringToken()
    {
        // Arrange
        string line = @"find-refs """"";

        // Act
        string[] result = LineTokenizer.Tokenize(line);

        // Assert
        result.ShouldBe(["find-refs", ""]);
    }

    [Fact]
    public void MultipleConsecutiveSpaces_AreIgnored()
    {
        // Arrange
        string line = "find-refs   MyClass   solution.sln";

        // Act
        string[] result = LineTokenizer.Tokenize(line);

        // Assert
        result.ShouldBe(["find-refs", "MyClass", "solution.sln"]);
    }

    [Fact]
    public void EmptyString_ReturnsEmptyArray()
    {
        // Arrange
        string line = "";

        // Act
        string[] result = LineTokenizer.Tokenize(line);

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public void WhitespaceOnly_ReturnsEmptyArray()
    {
        // Arrange
        string line = "   ";

        // Act
        string[] result = LineTokenizer.Tokenize(line);

        // Assert
        result.ShouldBeEmpty();
    }
}
