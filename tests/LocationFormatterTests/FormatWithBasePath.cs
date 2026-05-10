using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.LocationFormatterTests;

public sealed class FormatWithBasePath : IDisposable
{
    private readonly string _tempDir;
    private readonly string _absolutePath;

    public FormatWithBasePath()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"rq-tests-{Guid.NewGuid()}");
        string srcDir = Path.Combine(_tempDir, "src");
        Directory.CreateDirectory(srcDir);
        _absolutePath = Path.Combine(srcDir, "Foo.cs");
        File.WriteAllText(_absolutePath, "class Foo { }");
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void WhenPathExistsOnDisk_FormatsNormally()
    {
        // Arrange
        FileLinePositionSpan span = new(
            _absolutePath,
            new LinePosition(0, 0),
            new LinePosition(0, 5));

        // Act
        string? result = LocationFormatter.Format(span, context: false, tree: null);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldBe($"{_absolutePath}:1");
    }

    [Fact]
    public void WhenBasePathProvided_ReturnsRelativePath()
    {
        // Arrange
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            "class Foo { }",
            path: _absolutePath);
        FileLinePositionSpan span = tree.GetRoot()
            .GetLocation()
            .GetLineSpan();

        // Act
        string? result = LocationFormatter.Format(
            span,
            context: false,
            tree,
            basePath: _tempDir);

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
            path: _absolutePath);
        FileLinePositionSpan span = tree.GetRoot()
            .GetLocation()
            .GetLineSpan();

        // Act
        string? result = LocationFormatter.Format(
            span,
            context: false,
            tree,
            basePath: null);

        // Assert
        result.ShouldBe($"{_absolutePath}:1");
    }

    [Fact]
    public void WhenBasePathProvidedWithContext_ReturnsRelativePathWithSourceLine()
    {
        // Arrange
        string source = "    class Foo { }";
        File.WriteAllText(_absolutePath, source);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, path: _absolutePath);
        FileLinePositionSpan span = tree.GetRoot()
            .GetLocation()
            .GetLineSpan();

        // Act
        string? result = LocationFormatter.Format(
            span,
            context: true,
            tree,
            basePath: _tempDir);

        // Assert
        string expected = Path.Combine("src", "Foo.cs");
        result.ShouldBe($"{expected}:1\tclass Foo {{ }}");
    }
}