using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.MemberFormatterTests;

public sealed class FormatMembers
{
    [Fact]
    public void WhenInheritedIsFalse_ReturnsOwnMembersOnly()
    {
        // Arrange
        string source = @"
class Base
{
    public void BaseMethod() { }
}
class Derived : Base
{
    public int MyProp { get; set; }
    public void MyMethod(string name) { }
}";
        INamedTypeSymbol type = GetType(source, "Derived");

        // Act
        IReadOnlyList<string> result = MemberFormatter.FormatMembers(type, inherited: false);

        // Assert
        result.ShouldContain("property\tint MyProp");
        result.ShouldContain("method\tvoid MyMethod(string name)");
        result.ShouldNotContain(line => line.Contains("BaseMethod"));
    }

    [Fact]
    public void WhenInheritedIsTrue_IncludesBaseMembers()
    {
        // Arrange
        string source = @"
class Base
{
    public void BaseMethod() { }
}
class Derived : Base
{
    public void MyMethod() { }
}";
        INamedTypeSymbol type = GetType(source, "Derived");

        // Act
        IReadOnlyList<string> result = MemberFormatter.FormatMembers(type, inherited: true);

        // Assert
        result.ShouldContain("method\tvoid MyMethod()");
        result.ShouldContain("method\tvoid BaseMethod()\tBase");
    }

    [Fact]
    public void WhenInheritedIsTrue_IncludesObjectMembers()
    {
        // Arrange
        string source = @"
class MyClass
{
    public void MyMethod() { }
}";
        INamedTypeSymbol type = GetType(source, "MyClass");

        // Act
        IReadOnlyList<string> result = MemberFormatter.FormatMembers(type, inherited: true);

        // Assert
        result.ShouldContain(line => line.StartsWith("method\tstring") && line.Contains("ToString()"));
        result.ShouldContain(line => line.Contains("System.Object"));
    }

    [Fact]
    public void WhenInheritedIsTrue_WalksMultipleLevels()
    {
        // Arrange
        string source = @"
class GrandBase
{
    public void GrandMethod() { }
}
class Base : GrandBase
{
    public void BaseMethod() { }
}
class Derived : Base
{
    public void MyMethod() { }
}";
        INamedTypeSymbol type = GetType(source, "Derived");

        // Act
        IReadOnlyList<string> result = MemberFormatter.FormatMembers(type, inherited: true);

        // Assert
        result.ShouldContain("method\tvoid MyMethod()");
        result.ShouldContain("method\tvoid BaseMethod()\tBase");
        result.ShouldContain("method\tvoid GrandMethod()\tGrandBase");
    }

    [Fact]
    public void WhenInheritedIsTrue_ExcludesImplicitlyDeclaredMembers()
    {
        // Arrange
        string source = @"
class Base
{
    public int Value { get; set; }
}
class Derived : Base
{
    public void MyMethod() { }
}";
        INamedTypeSymbol type = GetType(source, "Derived");

        // Act
        IReadOnlyList<string> result = MemberFormatter.FormatMembers(type, inherited: true);

        // Assert
        result.ShouldContain("property\tint Value\tBase");
        result.ShouldNotContain(line => line.Contains("get_Value") || line.Contains("set_Value"));
    }

    [Fact]
    public void WhenInheritedIsFalse_ExcludesImplicitlyDeclaredMembers()
    {
        // Arrange
        string source = @"
class MyClass
{
    public int Value { get; set; }
}";
        INamedTypeSymbol type = GetType(source, "MyClass");

        // Act
        IReadOnlyList<string> result = MemberFormatter.FormatMembers(type, inherited: false);

        // Assert
        result.ShouldContain("property\tint Value");
        result.ShouldNotContain(line => line.Contains("get_Value") || line.Contains("set_Value"));
    }

    [Fact]
    public void WhenInheritedIsTrue_FormatsToStringFromObject()
    {
        // Arrange
        string source = @"
class MyClass { }";
        INamedTypeSymbol type = GetType(source, "MyClass");

        // Act
        IReadOnlyList<string> result = MemberFormatter.FormatMembers(type, inherited: true);

        // Assert
        result.ShouldContain(line => line.Contains("ToString()") && line.EndsWith("\tSystem.Object"));
    }

    private static INamedTypeSymbol GetType(string source, string typeName)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "TestAssembly",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        INamedTypeSymbol type = compilation.GetTypeByMetadataName(typeName)!;
        return type;
    }
}
