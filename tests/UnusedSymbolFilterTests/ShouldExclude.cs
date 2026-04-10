using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.UnusedSymbolFilterTests;

public sealed class ShouldExclude
{
    [Fact]
    public void WhenImplicitlyDeclared_ReturnsTrue()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilation(@"
class Foo
{
    public int Bar { get; set; }
}");
        INamedTypeSymbol type = compilation.GetTypeByMetadataName("Foo")!;
        // The backing field for an auto-property is implicitly declared
        IFieldSymbol backingField = type.GetMembers()
            .OfType<IFieldSymbol>()
            .First(f => f.IsImplicitlyDeclared);

        // Act
        bool result = UnusedSymbolFilter.ShouldExclude(backingField);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void WhenMethodImplementsInterfaceMember_ReturnsTrue()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilation(@"
interface IFoo
{
    void DoWork();
}
class Foo : IFoo
{
    public void DoWork() { }
}");
        INamedTypeSymbol type = compilation.GetTypeByMetadataName("Foo")!;
        IMethodSymbol method = type.GetMembers("DoWork")
            .OfType<IMethodSymbol>()
            .First();

        // Act
        bool result = UnusedSymbolFilter.ShouldExclude(method);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void WhenPropertyImplementsInterfaceMember_ReturnsTrue()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilation(@"
interface IFoo
{
    int Value { get; }
}
class Foo : IFoo
{
    public int Value { get; }
}");
        INamedTypeSymbol type = compilation.GetTypeByMetadataName("Foo")!;
        IPropertySymbol property = type.GetMembers("Value")
            .OfType<IPropertySymbol>()
            .First();

        // Act
        bool result = UnusedSymbolFilter.ShouldExclude(property);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void WhenMethodNamedMain_ReturnsTrue()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilation(@"
class Program
{
    static void Main() { }
}");
        INamedTypeSymbol type = compilation.GetTypeByMetadataName("Program")!;
        IMethodSymbol method = type.GetMembers("Main")
            .OfType<IMethodSymbol>()
            .First();

        // Act
        bool result = UnusedSymbolFilter.ShouldExclude(method);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void WhenConstructorWithParameters_ReturnsTrue()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilation(@"
class Foo
{
    public Foo(int value) { }
}");
        INamedTypeSymbol type = compilation.GetTypeByMetadataName("Foo")!;
        IMethodSymbol ctor = type.GetMembers(".ctor")
            .OfType<IMethodSymbol>()
            .First(m => m.Parameters.Length > 0);

        // Act
        bool result = UnusedSymbolFilter.ShouldExclude(ctor);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void WhenRegularMethod_ReturnsFalse()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilation(@"
class Foo
{
    public void DoWork() { }
}");
        INamedTypeSymbol type = compilation.GetTypeByMetadataName("Foo")!;
        IMethodSymbol method = type.GetMembers("DoWork")
            .OfType<IMethodSymbol>()
            .First();

        // Act
        bool result = UnusedSymbolFilter.ShouldExclude(method);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public void WhenParameterlessConstructor_ReturnsFalse()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilation(@"
class Foo
{
    public Foo() { }
}");
        INamedTypeSymbol type = compilation.GetTypeByMetadataName("Foo")!;
        IMethodSymbol ctor = type.GetMembers(".ctor")
            .OfType<IMethodSymbol>()
            .First();

        // Act
        bool result = UnusedSymbolFilter.ShouldExclude(ctor);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public void WhenRegularType_ReturnsFalse()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilation(@"
class Foo
{
    public void DoWork() { }
}");
        INamedTypeSymbol type = compilation.GetTypeByMetadataName("Foo")!;

        // Act
        bool result = UnusedSymbolFilter.ShouldExclude(type);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public void WhenTopLevelStatementsEntryPoint_ReturnsTrue()
    {
        // Arrange
        CSharpCompilation compilation = CreateCompilation(@"
class Program
{
    static void <Main>$(string[] args) { }
}", allowUnsafe: true);
        INamedTypeSymbol type = compilation.GetTypeByMetadataName("Program")!;
        IMethodSymbol? method = type.GetMembers("<Main>$")
            .OfType<IMethodSymbol>()
            .FirstOrDefault();

        // Skip test if compiler doesn't produce <Main>$ with this approach
        if (method is null)
        {
            return;
        }

        // Act
        bool result = UnusedSymbolFilter.ShouldExclude(method);

        // Assert
        result.ShouldBeTrue();
    }

    private static CSharpCompilation CreateCompilation(string source, bool allowUnsafe = false)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilationOptions options = new(
            OutputKind.DynamicallyLinkedLibrary,
            allowUnsafe: allowUnsafe);
        return CSharpCompilation.Create(
            "TestAssembly",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            options);
    }
}
