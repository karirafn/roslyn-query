using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.MetadataTypeResolverTests;

public sealed class FindMetadataTypes
{
    [Fact]
    public void WhenMetadataTypeExists_ReturnsMatchingType()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilationWithReferences("class Dummy { }");

        // Act
        IReadOnlyList<INamedTypeSymbol> result = MetadataTypeResolver.FindMetadataTypes(
            [compilation],
            "Object");

        // Assert
        result.Count.ShouldBe(1);
        result[0].ToDisplayString().ShouldBe("object");
    }

    [Fact]
    public void WhenTypeNotFound_ReturnsEmptyList()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilationWithReferences("class Dummy { }");

        // Act
        IReadOnlyList<INamedTypeSymbol> result = MetadataTypeResolver.FindMetadataTypes(
            [compilation],
            "NonExistentType12345");

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public void WhenMultipleMetadataTypesMatch_ReturnsAllDistinct()
    {
        // Arrange
        string source1 = "namespace Ns1 { class MyType { } }";
        string source2 = "namespace Ns2 { class MyType { } }";
        CSharpCompilation compilation1 = CreateCompilationWithReferences(source1, "Assembly1");
        CSharpCompilation compilation2 = CreateCompilationWithReferences(source2, "Assembly2");

        // Use compilation1 as a reference in a third compilation so MyType appears as metadata
        MetadataReference ref1 = compilation1.ToMetadataReference();
        MetadataReference ref2 = compilation2.ToMetadataReference();
        CSharpCompilation consumer = CSharpCompilation.Create(
            "Consumer",
            [CSharpSyntaxTree.ParseText("class Stub { }")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location), ref1, ref2]);

        // Act
        IReadOnlyList<INamedTypeSymbol> result = MetadataTypeResolver.FindMetadataTypes(
            [consumer],
            "MyType");

        // Assert
        result.Count.ShouldBe(2);
    }

    [Fact]
    public void WhenSourceTypeExists_ExcludesIt()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilationWithReferences("class Object { }");

        // Act - searching for "Object" should only return metadata types, not source types
        IReadOnlyList<INamedTypeSymbol> result = MetadataTypeResolver.FindMetadataTypes(
            [compilation],
            "Object");

        // Assert - should find System.Object from metadata, not the source "Object"
        result.ShouldAllBe(t => !t.Locations.Any(l => l.IsInSource));
    }

    [Fact]
    public void WhenDuplicateAcrossCompilations_ReturnsDistinct()
    {
        // Arrange - two compilations referencing the same assembly
        CSharpCompilation compilation1 = CreateCompilationWithReferences("class A { }");
        CSharpCompilation compilation2 = CreateCompilationWithReferences("class B { }");

        // Act
        IReadOnlyList<INamedTypeSymbol> result = MetadataTypeResolver.FindMetadataTypes(
            [compilation1, compilation2],
            "Object");

        // Assert - should deduplicate
        result.Count.ShouldBe(1);
    }

    private static CSharpCompilation CreateCompilationWithReferences(
        string source,
        string assemblyName = "TestAssembly")
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        return CSharpCompilation.Create(
            assemblyName,
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
    }
}
