using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Shouldly;

namespace roslyn_query.Tests.AttributeScannerTests;

public sealed class ScanCompilation
{
    [Fact]
    public void WhenTypeHasMatchingAttribute_ReturnsResult()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilation(@"
using System;
[Serializable]
class Foo { }
");

        // Act
        List<AttributeMatch> results = AttributeScanner.ScanCompilation(compilation, "Serializable");

        // Assert
        results.Count.ShouldBe(1);
        results[0].FullyQualifiedName.ShouldContain("Foo");
    }

    [Fact]
    public void WhenMemberHasMatchingAttribute_ReturnsResult()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilation(@"
using System;
class Foo
{
    [Obsolete]
    public void Bar() { }
}
");

        // Act
        List<AttributeMatch> results = AttributeScanner.ScanCompilation(compilation, "Obsolete");

        // Assert
        results.Count.ShouldBe(1);
        results[0].FullyQualifiedName.ShouldContain("Bar");
    }

    [Fact]
    public void WhenNoMatchingAttribute_ReturnsEmpty()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilation(@"
class Foo
{
    public void Bar() { }
}
");

        // Act
        List<AttributeMatch> results = AttributeScanner.ScanCompilation(compilation, "Obsolete");

        // Assert
        results.ShouldBeEmpty();
    }

    [Fact]
    public void WhenAttributeNameUsedWithSuffix_MatchesWithoutSuffix()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilation(@"
using System;
[Serializable]
class Foo { }
");

        // Act
        List<AttributeMatch> results = AttributeScanner.ScanCompilation(
            compilation,
            "SerializableAttribute");

        // Assert
        results.Count.ShouldBe(1);
    }

    [Fact]
    public void WhenMultipleSymbolsHaveAttribute_ReturnsAll()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilation(@"
using System;
[Obsolete]
class Foo
{
    [Obsolete]
    public void Bar() { }

    [Obsolete]
    public int Baz { get; set; }
}
");

        // Act
        List<AttributeMatch> results = AttributeScanner.ScanCompilation(compilation, "Obsolete");

        // Assert
        results.Count.ShouldBe(3);
    }

    [Fact]
    public void WhenDuplicateLocations_DeduplicatesByPathAndLine()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilation(@"
using System;
[Obsolete]
class Foo { }
");

        // Act — scan twice and merge, simulating multi-project overlap
        List<AttributeMatch> results1 = AttributeScanner.ScanCompilation(compilation, "Obsolete");
        List<AttributeMatch> results2 = AttributeScanner.ScanCompilation(compilation, "Obsolete");
        List<AttributeMatch> merged = AttributeScanner.DeduplicateAndSort(
            [.. results1, .. results2]);

        // Assert
        merged.Count.ShouldBe(1);
    }

    [Fact]
    public void WhenResultsFromMultipleSources_SortsByPathThenLine()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilation(@"
using System;
[Obsolete]
class Zzz { }

[Obsolete]
class Aaa { }
");

        // Act
        List<AttributeMatch> results = AttributeScanner.ScanCompilation(compilation, "Obsolete");
        List<AttributeMatch> sorted = AttributeScanner.DeduplicateAndSort(results);

        // Assert — Aaa comes before Zzz (by line number since same file)
        sorted.Count.ShouldBe(2);
        sorted[0].FullyQualifiedName.ShouldContain("Zzz");
        sorted[1].FullyQualifiedName.ShouldContain("Aaa");
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilationOptions options = new(OutputKind.DynamicallyLinkedLibrary);
        return CSharpCompilation.Create(
            "TestAssembly",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            options);
    }
}
