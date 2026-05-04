using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.TrackedFilesTests;

public sealed class CollectPaths
{
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void WhenSolutionFilePathIsNullOrEmpty_Throws(string? solutionFilePath)
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;

        // Act / Assert
        Should.Throw<ArgumentException>(
            () => TrackedFiles.CollectPaths(solution, "dir", solutionFilePath!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void WhenSolutionDirectoryIsNullOrEmpty_Throws(string? solutionDirectory)
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;

        // Act / Assert
        Should.Throw<ArgumentException>(
            () => TrackedFiles.CollectPaths(solution, solutionDirectory!, "Solution.sln"));
    }

    [Fact]
    public void AlwaysIncludesSolutionFilePath()
    {
        // Arrange
        string solutionDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(solutionDir);

        try
        {
            string solutionPath = Path.Combine(solutionDir, "Solution.sln");
            File.WriteAllText(solutionPath, "");

            using AdhocWorkspace workspace = new();
            Solution solution = workspace.CurrentSolution;

            // Act
            IReadOnlyList<string> paths = TrackedFiles.CollectPaths(solution, solutionDir, solutionPath);

            // Assert
            paths.ShouldContain(solutionPath);
        }
        finally
        {
            Directory.Delete(solutionDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("Directory.Packages.props")]
    [InlineData("Directory.Build.props")]
    public void WhenPropsFileExistsInSolutionDirectory_IncludesIt(string propsFileName)
    {
        // Arrange
        string solutionDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(solutionDir);

        try
        {
            string propsPath = Path.Combine(solutionDir, propsFileName);
            File.WriteAllText(propsPath, "<Project />");

            string solutionPath = Path.Combine(solutionDir, "Solution.sln");

            using AdhocWorkspace workspace = new();
            Solution solution = workspace.CurrentSolution;

            // Act
            IReadOnlyList<string> paths = TrackedFiles.CollectPaths(solution, solutionDir, solutionPath);

            // Assert
            paths.ShouldContain(propsPath);
        }
        finally
        {
            Directory.Delete(solutionDir, recursive: true);
        }
    }

    [Fact]
    public void WhenProjectHasNoFilePath_OnlyContainsSolutionFile()
    {
        // Arrange
        string solutionPath = @"C:\repos\Solution.sln";
        string solutionDir = @"C:\repos";

        using AdhocWorkspace workspace = new();
        Solution solution = workspace.AddProject(ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Create(),
            "Alpha",
            "Alpha",
            LanguageNames.CSharp,
            filePath: null))
            .Solution;

        // Act
        IReadOnlyList<string> paths = TrackedFiles.CollectPaths(solution, solutionDir, solutionPath);

        // Assert
        paths.ShouldBe([solutionPath]);
    }

    [Fact]
    public void WhenPropsFileExistsInParentDirectory_IncludesIt()
    {
        // Arrange
        string rootDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string solutionDir = Path.Combine(rootDir, "solution");
        Directory.CreateDirectory(solutionDir);

        try
        {
            string propsPath = Path.Combine(rootDir, "Directory.Packages.props");
            File.WriteAllText(propsPath, "<Project />");

            string solutionPath = Path.Combine(solutionDir, "Solution.sln");

            using AdhocWorkspace workspace = new();
            Solution solution = workspace.CurrentSolution;

            // Act
            IReadOnlyList<string> paths = TrackedFiles.CollectPaths(solution, solutionDir, solutionPath);

            // Assert
            paths.ShouldContain(propsPath);
        }
        finally
        {
            Directory.Delete(rootDir, recursive: true);
        }
    }

    [Fact]
    public void WhenSolutionHasProjects_IncludesCsprojPaths()
    {
        // Arrange
        string solutionDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(solutionDir);

        try
        {
            string solutionPath = Path.Combine(solutionDir, "Solution.sln");
            string csprojPath = Path.Combine(solutionDir, "Alpha", "Alpha.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(csprojPath)!);
            File.WriteAllText(csprojPath, "<Project />");

            using AdhocWorkspace workspace = new();
            Solution solution = workspace.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Create(),
                "Alpha",
                "Alpha",
                LanguageNames.CSharp,
                filePath: csprojPath))
                .Solution;

            // Act
            IReadOnlyList<string> paths = TrackedFiles.CollectPaths(solution, solutionDir, solutionPath);

            // Assert
            paths.ShouldContain(csprojPath);
        }
        finally
        {
            Directory.Delete(solutionDir, recursive: true);
        }
    }

    [Fact]
    public void WhenProjectFileDoesNotExistOnDisk_ExcludesCsprojPath()
    {
        // Arrange
        string solutionDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(solutionDir);

        try
        {
            string solutionPath = Path.Combine(solutionDir, "Solution.sln");
            File.WriteAllText(solutionPath, "");

            string deletedCsprojPath = Path.Combine(solutionDir, "Deleted", "Deleted.csproj");

            using AdhocWorkspace workspace = new();
            Solution solution = workspace.AddProject(ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Create(),
                "Deleted",
                "Deleted",
                LanguageNames.CSharp,
                filePath: deletedCsprojPath))
                .Solution;

            // Act
            IReadOnlyList<string> paths = TrackedFiles.CollectPaths(solution, solutionDir, solutionPath);

            // Assert
            paths.ShouldNotContain(deletedCsprojPath);
            paths.ShouldContain(solutionPath);
        }
        finally
        {
            Directory.Delete(solutionDir, recursive: true);
        }
    }
}
