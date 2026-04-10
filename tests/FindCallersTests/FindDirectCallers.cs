using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.FindCallersTests;

public sealed class FindDirectCallers
{
    [Fact]
    public async Task WhenMethodHasDirectCallers_ReturnsOnlyDirectCallers()
    {
        // Arrange
        string source = @"
class Foo
{
    public void Target() { }
    public void Caller1() { Target(); }
    public void Caller2() { Target(); }
}";
        (Solution solution, IMethodSymbol target) = await CreateSolutionWithMethod(source, "Foo", "Target");

        // Act
        IEnumerable<SymbolCallerInfo> callers = await SymbolFinder.FindCallersAsync(target, solution);
        IReadOnlyList<SymbolCallerInfo> directCallers = CallerFilter.GetDirectCallers(callers);

        // Assert
        directCallers.Count.ShouldBe(2);
        directCallers.ShouldAllBe(c => c.IsDirect);
    }

    [Fact]
    public async Task WhenMethodHasNoCallers_ReturnsEmpty()
    {
        // Arrange
        string source = @"
class Foo
{
    public void Target() { }
}";
        (Solution solution, IMethodSymbol target) = await CreateSolutionWithMethod(source, "Foo", "Target");

        // Act
        IEnumerable<SymbolCallerInfo> callers = await SymbolFinder.FindCallersAsync(target, solution);
        IReadOnlyList<SymbolCallerInfo> directCallers = CallerFilter.GetDirectCallers(callers);

        // Assert
        directCallers.ShouldBeEmpty();
    }

    [Fact]
    public async Task WhenMethodIsCalledIndirectlyViaDelegate_ExcludesIndirectCallers()
    {
        // Arrange
        string source = @"
using System;
class Foo
{
    public void Target() { }
    public void DirectCaller() { Target(); }
    public void IndirectUser() { Action a = Target; }
}";
        (Solution solution, IMethodSymbol target) = await CreateSolutionWithMethod(source, "Foo", "Target");

        // Act
        IEnumerable<SymbolCallerInfo> callers = await SymbolFinder.FindCallersAsync(target, solution);
        IReadOnlyList<SymbolCallerInfo> directCallers = CallerFilter.GetDirectCallers(callers);

        // Assert
        directCallers.ShouldAllBe(c => c.IsDirect);
        List<string> callerNames = directCallers
            .Select(c => c.CallingSymbol.Name)
            .ToList();
        callerNames.ShouldContain("DirectCaller");
    }

    private static async Task<(Solution, IMethodSymbol)> CreateSolutionWithMethod(
        string source,
        string typeName,
        string methodName)
    {
        AdhocWorkspace workspace = new();
        ProjectInfo projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            "TestProject",
            "TestProject",
            LanguageNames.CSharp,
            metadataReferences:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            ]);
        Project project = workspace.AddProject(projectInfo);
        Document document = workspace.AddDocument(project.Id, "Test.cs", Microsoft.CodeAnalysis.Text.SourceText.From(source));
        Solution solution = document.Project.Solution;
        Compilation compilation = (await document.Project.GetCompilationAsync())!;
        INamedTypeSymbol type = compilation.GetTypeByMetadataName(typeName)!;
        IMethodSymbol method = type.GetMembers(methodName)
            .OfType<IMethodSymbol>()
            .First();
        return (solution, method);
    }
}
