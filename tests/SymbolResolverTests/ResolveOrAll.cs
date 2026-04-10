using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Shouldly;

namespace roslyn_query.Tests.SymbolResolverTests;

public sealed class ResolveOrAll
{
    [Fact]
    public void WhenNoSymbolsAndAllIsFalse_ReturnsErrorExitCode()
    {
        // Arrange
        List<ISymbol> candidates = [];
        StringWriter stderr = new();

        // Act
        SymbolResolverResult result = SymbolResolver.ResolveOrAll(
            candidates,
            "Foo",
            all: false,
            stderr);

        // Assert
        result.ExitCode.ShouldBe(1);
        result.Symbols.ShouldBeEmpty();
        stderr.ToString().ShouldContain("Symbol not found: Foo");
    }

    [Fact]
    public void WhenNoSymbolsAndAllIsTrue_ReturnsEmptyWithExitCodeZero()
    {
        // Arrange
        List<ISymbol> candidates = [];
        StringWriter stderr = new();

        // Act
        SymbolResolverResult result = SymbolResolver.ResolveOrAll(
            candidates,
            "Foo",
            all: true,
            stderr);

        // Assert
        result.ExitCode.ShouldBe(0);
        result.Symbols.ShouldBeEmpty();
        stderr.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void WhenOneSymbol_ReturnsIt()
    {
        // Arrange
        ISymbol symbol = CreateSymbol("Foo");
        List<ISymbol> candidates = [symbol];
        StringWriter stderr = new();

        // Act
        SymbolResolverResult result = SymbolResolver.ResolveOrAll(
            candidates,
            "Foo",
            all: false,
            stderr);

        // Assert
        result.ExitCode.ShouldBe(0);
        result.Symbols.Count.ShouldBe(1);
        result.Symbols[0].ShouldBeSameAs(symbol);
    }

    [Fact]
    public void WhenMultipleSymbolsAndAllIsFalse_ReturnsAmbiguityError()
    {
        // Arrange
        List<ISymbol> candidates = [CreateSymbol("Foo"), CreateSymbol("Bar")];
        StringWriter stderr = new();

        // Act
        SymbolResolverResult result = SymbolResolver.ResolveOrAll(
            candidates,
            "Foo",
            all: false,
            stderr);

        // Assert
        result.ExitCode.ShouldBe(1);
        result.Symbols.ShouldBeEmpty();
        string error = stderr.ToString();
        error.ShouldContain("Ambiguous");
        error.ShouldContain("2 matches");
        error.ShouldContain("Use TypeName.MemberName to disambiguate.");
    }

    [Fact]
    public void WhenMultipleSymbolsAndAllIsTrue_ReturnsAll()
    {
        // Arrange
        ISymbol first = CreateSymbol("Foo");
        ISymbol second = CreateSymbol("Bar");
        List<ISymbol> candidates = [first, second];
        StringWriter stderr = new();

        // Act
        SymbolResolverResult result = SymbolResolver.ResolveOrAll(
            candidates,
            "Foo",
            all: true,
            stderr);

        // Assert
        result.ExitCode.ShouldBe(0);
        result.Symbols.Count.ShouldBe(2);
        result.Symbols[0].ShouldBeSameAs(first);
        result.Symbols[1].ShouldBeSameAs(second);
    }

    private static ISymbol CreateSymbol(string name)
    {
        string source = $"class {name} {{ }}";
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Test",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        return compilation.GetTypeByMetadataName(name)!;
    }
}
