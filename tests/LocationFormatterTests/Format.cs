using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.LocationFormatterTests;

public sealed class Format
{
    private const string TestFilePath = "Test.cs";

    [Fact]
    public void WhenContextIsFalse_ReturnsPathAndLineOnly()
    {
        // Arrange
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            "class Foo { }",
            path: TestFilePath);
        FileLinePositionSpan span = tree.GetRoot()
            .GetLocation()
            .GetLineSpan();

        // Act
        string result = LocationFormatter.Format(span, context: false, tree);

        // Assert
        result.ShouldBe($"{TestFilePath}:1");
    }

    [Fact]
    public void WhenContextIsTrueAndTreeIsNull_ReturnsPathAndLineOnly()
    {
        // Arrange
        FileLinePositionSpan span = new(
            TestFilePath,
            new LinePosition(0, 0),
            new LinePosition(0, 5));

        // Act
        string result = LocationFormatter.Format(span, context: true, tree: null);

        // Assert
        result.ShouldBe($"{TestFilePath}:1");
    }

    [Fact]
    public void WhenContextIsTrue_AppendsTrimmmedSourceLine()
    {
        // Arrange
        string source = "    class Foo { }";
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, path: TestFilePath);
        FileLinePositionSpan span = tree.GetRoot()
            .GetLocation()
            .GetLineSpan();

        // Act
        string result = LocationFormatter.Format(span, context: true, tree);

        // Assert
        result.ShouldBe($"{TestFilePath}:1\tclass Foo {{ }}");
    }

    [Fact]
    public void WhenContextIsTrue_WithMultipleLines_ReturnsCorrectLine()
    {
        // Arrange
        string source = "using System;\nnamespace Foo\n{\n    class Bar { }\n}";
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, path: TestFilePath);
        FileLinePositionSpan span = new(
            TestFilePath,
            new LinePosition(3, 4),
            new LinePosition(3, 18));

        // Act
        string result = LocationFormatter.Format(span, context: true, tree);

        // Assert
        result.ShouldBe($"{TestFilePath}:4\tclass Bar {{ }}");
    }

    [Fact]
    public void WhenLineNumberExceedsLineCount_ReturnsPathAndLineOnly()
    {
        // Arrange
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            "class Foo { }",
            path: TestFilePath);
        FileLinePositionSpan span = new(
            TestFilePath,
            new LinePosition(99, 0),
            new LinePosition(99, 5));

        // Act
        string result = LocationFormatter.Format(span, context: true, tree);

        // Assert
        result.ShouldBe($"{TestFilePath}:100");
    }
}
