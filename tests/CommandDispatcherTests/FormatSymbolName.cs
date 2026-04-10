using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.CommandDispatcherTests;

public sealed class FormatSymbolName
{
    [Fact]
    public void WhenCompactIsTrue_ReturnsContainingTypeDotName()
    {
        // Arrange
        string source = @"
namespace MyApp
{
    public class OrderService
    {
        public void Process() { }
    }
}";
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "TestAssembly",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        INamedTypeSymbol type = compilation.GetTypeByMetadataName("MyApp.OrderService")!;
        ISymbol method = type.GetMembers("Process").First();

        // Act
        string result = CommandDispatcher.FormatSymbolName(method, compact: true);

        // Assert
        result.ShouldBe("OrderService.Process");
    }

    [Fact]
    public void WhenCompactIsFalse_ReturnsFullDisplayString()
    {
        // Arrange
        string source = @"
namespace MyApp
{
    public class OrderService
    {
        public void Process() { }
    }
}";
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "TestAssembly",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        INamedTypeSymbol type = compilation.GetTypeByMetadataName("MyApp.OrderService")!;
        ISymbol method = type.GetMembers("Process").First();

        // Act
        string result = CommandDispatcher.FormatSymbolName(method, compact: false);

        // Assert
        result.ShouldBe("MyApp.OrderService.Process()");
    }
}
