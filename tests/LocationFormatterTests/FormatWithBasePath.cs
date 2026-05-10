using System.Collections.Frozen;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.LocationFormatterTests;

public sealed class FormatWithBasePath
{
    private static readonly string AbsolutePath = Path.Combine(Path.GetTempPath(), "my-solution", "src", "Foo.cs");
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "my-solution");
    private static readonly FrozenSet<string> DocumentPaths =
        new[] { AbsolutePath }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void WhenPathIsInDocumentSet_FormatsNormally()
    {
        // Arrange
        FileLinePositionSpan span = new(
            AbsolutePath,
            new LinePosition(0, 0),
            new LinePosition(0, 5));

        // Act
        string? result = LocationFormatter.Format(span, context: false, tree: null, DocumentPaths);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldBe($"{AbsolutePath}:1");
    }

    [Fact]
    public void WhenBasePathProvided_ReturnsRelativePath()
    {
        // Arrange
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            "class Foo { }",
            path: AbsolutePath);
        FileLinePositionSpan span = tree.GetRoot()
            .GetLocation()
            .GetLineSpan();

        // Act
        string? result = LocationFormatter.Format(
            span,
            context: false,
            tree,
            DocumentPaths,
            basePath: TempDir);

        // Assert
        string expected = Path.Combine("src", "Foo.cs");
        result.ShouldBe($"{expected}:1");
    }

    [Fact]
    public void WhenBasePathIsNull_ReturnsAbsolutePath()
    {
        // Arrange
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            "class Foo { }",
            path: AbsolutePath);
        FileLinePositionSpan span = tree.GetRoot()
            .GetLocation()
            .GetLineSpan();

        // Act
        string? result = LocationFormatter.Format(
            span,
            context: false,
            tree,
            DocumentPaths,
            basePath: null);

        // Assert
        result.ShouldBe($"{AbsolutePath}:1");
    }

    [Fact]
    public void WhenBasePathProvidedWithContext_ReturnsRelativePathWithSourceLine()
    {
        // Arrange
        string source = "    class Foo { }";
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, path: AbsolutePath);
        FileLinePositionSpan span = tree.GetRoot()
            .GetLocation()
            .GetLineSpan();

        // Act
        string? result = LocationFormatter.Format(
            span,
            context: true,
            tree,
            DocumentPaths,
            basePath: TempDir);

        // Assert
        string expected = Path.Combine("src", "Foo.cs");
        result.ShouldBe($"{expected}:1\tclass Foo {{ }}");
    }
}