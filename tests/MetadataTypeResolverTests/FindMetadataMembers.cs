using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.MetadataTypeResolverTests;

public sealed class FindMetadataMembers
{
    [Fact]
    public void WhenMemberExistsInMetadata_ReturnsMember()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilationWithReferences("class Dummy { }");

        // Act — System.String.Format exists in mscorlib/System.Runtime metadata
        IReadOnlyList<ISymbol> result = MetadataTypeResolver.FindMetadataMembers(
            [compilation],
            "Format",
            qualifier: null);

        // Assert
        result.ShouldNotBeEmpty();
        result.ShouldAllBe(s => s.Name == "Format");
    }

    [Fact]
    public void WhenMemberNotFound_ReturnsEmpty()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilationWithReferences("class Dummy { }");

        // Act
        IReadOnlyList<ISymbol> result = MetadataTypeResolver.FindMetadataMembers(
            [compilation],
            "NonExistentMember99999",
            qualifier: null);

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public void WhenQualifierProvided_FiltersToMatchingType()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilationWithReferences("class Dummy { }");

        // Act — only System.String.Format, not Console.Format or others
        IReadOnlyList<ISymbol> result = MetadataTypeResolver.FindMetadataMembers(
            [compilation],
            "Format",
            qualifier: "System.String");

        // Assert
        result.ShouldNotBeEmpty();
        result.ShouldAllBe(s => s.ContainingType.ToDisplayString() == "string");
    }

    [Fact]
    public void WhenDuplicateAcrossCompilations_ReturnsDistinct()
    {
        // Arrange — two compilations both referencing the same System.Runtime
        CSharpCompilation compilation1 = CreateCompilationWithReferences("class A { }");
        CSharpCompilation compilation2 = CreateCompilationWithReferences("class B { }");

        // Act
        IReadOnlyList<ISymbol> result = MetadataTypeResolver.FindMetadataMembers(
            [compilation1, compilation2],
            "Format",
            qualifier: "System.String");

        // Assert
        int singleCompilationCount = MetadataTypeResolver.FindMetadataMembers(
            [compilation1],
            "Format",
            qualifier: "System.String").Count;
        result.Count.ShouldBe(singleCompilationCount);
    }

    [Fact]
    public void WhenQualifierNamesNestedType_ReturnsMemberOnNestedType()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilationWithReferences("class Dummy { }");

        // Act — Dictionary<TKey, TValue>.KeyCollection is a nested type; CopyTo is a member of it
        IReadOnlyList<ISymbol> result = MetadataTypeResolver.FindMetadataMembers(
            [compilation],
            "CopyTo",
            qualifier: "System.Collections.Generic.Dictionary<TKey, TValue>.KeyCollection");

        // Assert
        result.ShouldNotBeEmpty();
        result.ShouldAllBe(s => s.ContainingType.Name == "KeyCollection");
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
