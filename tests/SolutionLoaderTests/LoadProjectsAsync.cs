using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.SolutionLoaderTests;

public sealed class LoadProjectsAsync
{
    [Fact]
    public async Task WhenProjectNotInWorkspace_OpensIt()
    {
        // Arrange
        string projectPath = @"C:\solution\src\Alpha\Alpha.csproj";
        using AdhocWorkspace workspace = new();
        List<string> opened = [];

        // Act
        await SolutionLoader.LoadProjectsAsync(
            workspace,
            [projectPath],
            (path, _) => { opened.Add(path); return Task.CompletedTask; },
            CancellationToken.None);

        // Assert
        opened.ShouldBe([projectPath]);
    }

    [Fact]
    public async Task WhenProjectAlreadyInWorkspace_SkipsIt()
    {
        // Arrange
        string projectPath = @"C:\solution\src\Alpha\Alpha.csproj";
        using AdhocWorkspace workspace = new();
        workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "Alpha",
            "Alpha",
            LanguageNames.CSharp,
            filePath: projectPath));
        List<string> opened = [];

        // Act
        await SolutionLoader.LoadProjectsAsync(
            workspace,
            [projectPath],
            (path, _) => { opened.Add(path); return Task.CompletedTask; },
            CancellationToken.None);

        // Assert
        opened.ShouldBeEmpty();
    }

    [Fact]
    public async Task WhenTransitiveDependencyAlsoListedDirectly_SkipsSecondOpen()
    {
        // This is the bug scenario: Beta is opened first and its open-delegate
        // simulates Roslyn loading Alpha as a project reference. When Alpha is
        // then encountered directly in the slnx list it must be skipped.

        // Arrange
        string alphaPath = @"C:\solution\src\Alpha\Alpha.csproj";
        string betaPath = @"C:\solution\src\Beta\Beta.csproj";
        using AdhocWorkspace workspace = new();
        List<string> opened = [];

        Task OpenProject(string path, CancellationToken _)
        {
            opened.Add(path);
            if (path == betaPath)
            {
                // Simulate Roslyn adding Alpha as Beta's transitive project reference.
                workspace.AddProject(ProjectInfo.Create(
                    ProjectId.CreateNewId(),
                    VersionStamp.Create(),
                    "Alpha",
                    "Alpha",
                    LanguageNames.CSharp,
                    filePath: alphaPath));
            }
            return Task.CompletedTask;
        }

        // Act — slnx lists Beta first, then Alpha
        await SolutionLoader.LoadProjectsAsync(
            workspace,
            [betaPath, alphaPath],
            OpenProject,
            CancellationToken.None);

        // Assert — Alpha was not opened a second time
        opened.ShouldBe([betaPath]);
    }

    [Fact]
    public async Task PathComparisonIsCaseInsensitive()
    {
        // Arrange
        string projectPath = @"C:\Solution\Src\Alpha\Alpha.csproj";
        string projectPathLower = @"C:\solution\src\alpha\alpha.csproj";
        using AdhocWorkspace workspace = new();
        workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "Alpha",
            "Alpha",
            LanguageNames.CSharp,
            filePath: projectPath));
        List<string> opened = [];

        // Act
        await SolutionLoader.LoadProjectsAsync(
            workspace,
            [projectPathLower],
            (path, _) => { opened.Add(path); return Task.CompletedTask; },
            CancellationToken.None);

        // Assert
        opened.ShouldBeEmpty();
    }
}
