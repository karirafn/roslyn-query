using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.LocationFormatterTests;

public sealed class FormatWithBasePath
{
    [Fact]
    public void WhenBasePathProvided_ReturnsRelativePath()
    {
        // Arrange
        string absolutePath = Path.Combine("C:", "projects", "myapp", "src", "Foo.cs");
        string basePath = Path.Combine("C:", "projects", "myapp");
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            "class Foo { }",
            path: absolutePath);
        FileLinePositionSpan span = tree.GetRoot()
            .GetLocation()
            .GetLineSpan();

        // Act
        string result = LocationFormatter.Format(
            span,
            context: false,
            tree,
            basePath: basePath);

        // Assert
        string expected = Path.Combine("src", "Foo.cs");
        result.ShouldBe($"{expected}:1");
    }

    [Fact]
    public void WhenBasePathIsNull_ReturnsAbsolutePath()
    {
        // Arrange
        string absolutePath = Path.Combine("C:", "projects", "myapp", "src", "Foo.cs");
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            "class Foo { }",
            path: absolutePath);
        FileLinePositionSpan span = tree.GetRoot()
            .GetLocation()
            .GetLineSpan();

        // Act
        string result = LocationFormatter.Format(
            span,
            context: false,
            tree,
            basePath: null);

        // Assert
        result.ShouldBe($"{absolutePath}:1");
    }

    [Fact]
    public void WhenBasePathProvidedWithContext_ReturnsRelativePathWithSourceLine()
    {
        // Arrange
        string absolutePath = Path.Combine("C:", "projects", "myapp", "src", "Foo.cs");
        string basePath = Path.Combine("C:", "projects", "myapp");
        string source = "    class Foo { }";
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, path: absolutePath);
        FileLinePositionSpan span = tree.GetRoot()
            .GetLocation()
            .GetLineSpan();

        // Act
        string result = LocationFormatter.Format(
            span,
            context: true,
            tree,
            basePath: basePath);

        // Assert
        string expected = Path.Combine("src", "Foo.cs");
        result.ShouldBe($"{expected}:1\tclass Foo {{ }}");
    }
}
