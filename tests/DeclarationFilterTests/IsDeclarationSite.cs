using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DeclarationFilterTests;

public sealed class IsDeclarationSite
{
    [Fact]
    public void WhenLocationMatchesDeclaration_ReturnsTrue()
    {
        // Arrange
        SyntaxTree tree = CSharpSyntaxTree.ParseText("class Foo { }", path: "Test.cs");
        TextSpan declarationSpan = tree.GetRoot().DescendantNodes()
            .First()
            .Span;
        Location declarationLocation = Location.Create(tree, declarationSpan);

        // Act
        bool result = DeclarationFilter.IsDeclarationSite(
            tree,
            declarationSpan,
            [declarationLocation]);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void WhenLocationDoesNotMatchDeclaration_ReturnsFalse()
    {
        // Arrange
        SyntaxTree declarationTree = CSharpSyntaxTree.ParseText("class Foo { }", path: "Decl.cs");
        SyntaxTree referenceTree = CSharpSyntaxTree.ParseText("class Bar : Foo { }", path: "Ref.cs");
        TextSpan declarationSpan = declarationTree.GetRoot().DescendantNodes()
            .First()
            .Span;
        Location declarationLocation = Location.Create(declarationTree, declarationSpan);
        TextSpan referenceSpan = referenceTree.GetRoot().DescendantNodes()
            .First()
            .Span;

        // Act
        bool result = DeclarationFilter.IsDeclarationSite(
            referenceTree,
            referenceSpan,
            [declarationLocation]);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public void WhenSameTreeButDifferentSpan_ReturnsFalse()
    {
        // Arrange
        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            "class Foo { } class Bar { }",
            path: "Test.cs");
        Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax[] types = tree.GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>()
            .ToArray();
        TextSpan declarationSpan = types[0].Identifier.Span;
        TextSpan referenceSpan = types[1].Identifier.Span;
        Location declarationLocation = Location.Create(tree, declarationSpan);

        // Act
        bool result = DeclarationFilter.IsDeclarationSite(
            tree,
            referenceSpan,
            [declarationLocation]);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public void WhenReferenceTreeIsNull_ReturnsFalse()
    {
        // Arrange
        SyntaxTree tree = CSharpSyntaxTree.ParseText("class Foo { }", path: "Test.cs");
        TextSpan span = tree.GetRoot().Span;
        Location declarationLocation = Location.Create(tree, span);

        // Act
        bool result = DeclarationFilter.IsDeclarationSite(
            null,
            span,
            [declarationLocation]);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public void WhenDeclarationLocationsIsEmpty_ReturnsFalse()
    {
        // Arrange
        SyntaxTree tree = CSharpSyntaxTree.ParseText("class Foo { }", path: "Test.cs");
        TextSpan span = tree.GetRoot().Span;

        // Act
        bool result = DeclarationFilter.IsDeclarationSite(
            tree,
            span,
            []);

        // Assert
        result.ShouldBeFalse();
    }
}
