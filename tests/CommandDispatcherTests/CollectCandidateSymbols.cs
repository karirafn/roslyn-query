using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.CommandDispatcherTests;

public sealed class CollectCandidateSymbols
{
    [Fact]
    public async Task WhenSameSymbolAppearsInCompilation_DeduplicatesBySymbolEquality()
    {
        // Arrange
        string source = @"
namespace TestProject;

public class Foo
{
    public void Bar() { }
}";
        AdhocWorkspace workspace = new();
        MetadataReference[] refs =
        [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        ];

        ProjectId projectId1 = ProjectId.CreateNewId();
        ProjectId projectId2 = ProjectId.CreateNewId();

        Solution solution = workspace.CurrentSolution
            .AddProject(projectId1, "Project1", "Project1", LanguageNames.CSharp)
            .AddMetadataReferences(projectId1, refs)
            .AddDocument(
                DocumentId.CreateNewId(projectId1),
                "Foo.cs",
                SourceText.From(source))
            .AddProject(projectId2, "Project2", "Project2", LanguageNames.CSharp)
            .AddMetadataReferences(projectId2, refs)
            .AddProjectReference(projectId2, new ProjectReference(projectId1));

        // Act
        List<ISymbol> candidates = await CommandDispatcher.CollectCandidateSymbols(solution);

        // Assert — Foo and Bar from Project1 appear only once despite two compilations
        List<string> displayNames = candidates
            .Select(s => s.ToDisplayString())
            .ToList();
        displayNames.Count.ShouldBe(displayNames.Distinct().Count());
    }

    [Fact]
    public async Task WhenSymbolsShouldBeExcluded_ExcludesThem()
    {
        // Arrange
        string source = @"
namespace TestProject;

public class Foo
{
    public int Value { get; set; }
    static void Main() { }
}";
        Solution solution = CreateSolution(("Test.cs", source));

        // Act
        List<ISymbol> candidates = await CommandDispatcher.CollectCandidateSymbols(solution);

        // Assert
        List<string> names = candidates
            .Select(s => s.Name)
            .ToList();
        names.ShouldNotContain("Main");
        names.ShouldNotContain("<Value>k__BackingField");
    }

    [Fact]
    public async Task WhenSourceTypesExist_IncludesTypesAndMembers()
    {
        // Arrange
        string source = @"
namespace TestProject;

public class Foo
{
    public void Bar() { }
}";
        Solution solution = CreateSolution(("Test.cs", source));

        // Act
        List<ISymbol> candidates = await CommandDispatcher.CollectCandidateSymbols(solution);

        // Assert
        List<string> names = candidates
            .Select(s => s.Name)
            .ToList();
        names.ShouldContain("Foo");
        names.ShouldContain("Bar");
    }

    [Fact]
    public async Task WhenCancelledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        Solution solution = CreateSolution(("Test.cs", "class C { }"));
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await CommandDispatcher.CollectCandidateSymbols(solution, cts.Token));
    }

    private static Solution CreateSolution(params (string FileName, string Source)[] files)
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

        foreach ((string fileName, string source) in files)
        {
            Document document = workspace.AddDocument(
                project.Id,
                fileName,
                SourceText.From(source));
            project = document.Project;
        }

        return project.Solution;
    }
}
