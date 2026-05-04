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

    [Fact]
    public void WhenProjectHasPackagesLockJson_IncludesIt()
    {
        // Arrange
        string solutionDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(solutionDir);

        try
        {
            string solutionPath = Path.Combine(solutionDir, "Solution.sln");
            string projectDir = Path.Combine(solutionDir, "Alpha");
            Directory.CreateDirectory(projectDir);
            string csprojPath = Path.Combine(projectDir, "Alpha.csproj");
            string lockFilePath = Path.Combine(projectDir, "packages.lock.json");
            File.WriteAllText(csprojPath, "<Project />");
            File.WriteAllText(lockFilePath, "{}");

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
            paths.ShouldContain(lockFilePath);
        }
        finally
        {
            Directory.Delete(solutionDir, recursive: true);
        }
    }

    [Fact]
    public void WhenPropsFileIsAboveGitRoot_DoesNotIncludeIt()
    {
        // Arrange
        // Structure: rootDir/above.props, rootDir/repo/.git, rootDir/repo/src/Solution.sln
        // The walk stops at rootDir/repo (the git root) and must not reach rootDir/above.props
        string rootDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string repoDir = Path.Combine(rootDir, "repo");
        string gitDir = Path.Combine(repoDir, ".git");
        string solutionDir = Path.Combine(repoDir, "src");
        Directory.CreateDirectory(gitDir);
        Directory.CreateDirectory(solutionDir);

        try
        {
            string aboveGitRootPropsPath = Path.Combine(rootDir, "Directory.Packages.props");
            File.WriteAllText(aboveGitRootPropsPath, "<Project />");

            string solutionPath = Path.Combine(solutionDir, "Solution.sln");

            using AdhocWorkspace workspace = new();
            Solution solution = workspace.CurrentSolution;

            // Act
            IReadOnlyList<string> paths = TrackedFiles.CollectPaths(solution, solutionDir, solutionPath);

            // Assert — the props file above the git root must not be tracked
            paths.ShouldNotContain(aboveGitRootPropsPath);
        }
        finally
        {
            Directory.Delete(rootDir, recursive: true);
        }
    }

    [Fact]
    public void WhenPropsFileIsAtGitRoot_IncludesIt()
    {
        // Arrange
        // Structure: rootDir/.git, rootDir/Directory.Packages.props, rootDir/src/Solution.sln
        // The walk should find the props file at the git root level and include it
        string rootDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string gitDir = Path.Combine(rootDir, ".git");
        string solutionDir = Path.Combine(rootDir, "src");
        Directory.CreateDirectory(gitDir);
        Directory.CreateDirectory(solutionDir);

        try
        {
            string propsAtGitRoot = Path.Combine(rootDir, "Directory.Packages.props");
            File.WriteAllText(propsAtGitRoot, "<Project />");

            string solutionPath = Path.Combine(solutionDir, "Solution.sln");

            using AdhocWorkspace workspace = new();
            Solution solution = workspace.CurrentSolution;

            // Act
            IReadOnlyList<string> paths = TrackedFiles.CollectPaths(solution, solutionDir, solutionPath);

            // Assert — the props file at the git root must be tracked
            paths.ShouldContain(propsAtGitRoot);
        }
        finally
        {
            Directory.Delete(rootDir, recursive: true);
        }
    }

    [Fact]
    public void WhenProjectHasNoPackagesLockJson_DoesNotIncludeIt()
    {
        // Arrange
        string solutionDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(solutionDir);

        try
        {
            string solutionPath = Path.Combine(solutionDir, "Solution.sln");
            string projectDir = Path.Combine(solutionDir, "Alpha");
            Directory.CreateDirectory(projectDir);
            string csprojPath = Path.Combine(projectDir, "Alpha.csproj");
            File.WriteAllText(csprojPath, "<Project />");
            // No packages.lock.json created

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
            string expectedLockPath = Path.Combine(projectDir, "packages.lock.json");
            paths.ShouldNotContain(expectedLockPath);
        }
        finally
        {
            Directory.Delete(solutionDir, recursive: true);
        }
    }
}
