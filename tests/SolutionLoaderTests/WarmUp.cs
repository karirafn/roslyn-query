using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.SolutionLoaderTests;

/// <summary>
/// Tests for SolutionLoader.WarmUpAsync — verifies that every project's compilation is
/// materialized (non-null GetCompilationAsync result) when WarmUpAsync is called.
/// Uses an in-memory AdhocWorkspace with metadata references so no MSBuild install is required.
/// </summary>
public sealed class WarmUp
{
    [Fact]
    public async Task WhenSolutionHasProjects_MaterializesCompilationForEach()
    {
        // Arrange
        using AdhocWorkspace workspace = new();

        ProjectInfo projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            "WarmUpLib",
            "WarmUpLib",
            LanguageNames.CSharp,
            metadataReferences:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            ]);

        workspace.AddProject(projectInfo);
        Solution solution = workspace.CurrentSolution;
        solution.Projects.ShouldNotBeEmpty();

        // Act
        await SolutionLoader.WarmUpAsync(solution, CancellationToken.None);

        // Assert — GetCompilationAsync for each project must return non-null
        foreach (Project project in solution.Projects)
        {
            Compilation? compilation = await project.GetCompilationAsync(CancellationToken.None);
            compilation.ShouldNotBeNull(
                $"Compilation for project '{project.Name}' should be non-null after WarmUpAsync.");
        }
    }

    [Fact]
    public async Task WhenSolutionHasNoProjects_CompletesWithoutError()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        solution.Projects.ShouldBeEmpty();

        // Act / Assert — should not throw
        await SolutionLoader.WarmUpAsync(solution, CancellationToken.None);
    }

    [Fact]
    public async Task WhenCancelled_ThrowsOperationCanceledException()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;

        ProjectInfo projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            "CancellationLib",
            "CancellationLib",
            LanguageNames.CSharp,
            metadataReferences:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            ]);

        workspace.AddProject(projectInfo);
        solution = workspace.CurrentSolution;

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        // Act / Assert
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await SolutionLoader.WarmUpAsync(solution, cts.Token));
    }
}
