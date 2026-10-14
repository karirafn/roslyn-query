using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.CommandDispatcherTests;

public sealed class ListProjectsCommand
{
    [Fact]
    public async Task WhenSolutionHasProjects_OutputsNameAndRelativePath()
    {
        // Arrange
        string solutionDir = Path.Combine(Path.GetTempPath(), "mysln");
        string projectPath = Path.Combine(solutionDir, "src", "MyApp", "MyApp.csproj");
        Solution solution = CreateSolutionWithProject("MyApp", projectPath);
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution, solutionDir);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["list-projects"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        string output = stdout.ToString().TrimEnd();
        string expectedRelative = Path.Combine("src", "MyApp", "MyApp.csproj");
        output.ShouldBe($"MyApp\t{expectedRelative}");
    }

    [Fact]
    public async Task WhenAbsoluteFlag_OutputsAbsolutePath()
    {
        // Arrange
        string solutionDir = Path.Combine(Path.GetTempPath(), "mysln");
        string projectPath = Path.Combine(solutionDir, "src", "MyApp", "MyApp.csproj");
        Solution solution = CreateSolutionWithProject("MyApp", projectPath);
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution, solutionDir);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["list-projects", "--absolute"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        string output = stdout.ToString().TrimEnd();
        output.ShouldBe($"MyApp\t{projectPath}");
    }

    [Fact]
    public async Task WhenEmptySolution_OutputsNothingOnStdoutAndCompleteOnStderr()
    {
        // Arrange
        Solution solution = CreateEmptySolution();
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["list-projects"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        stdout.ToString().ShouldBeEmpty();
        stderr.ToString().TrimEnd().ShouldBe("workspace: complete");
    }

    [Fact]
    public async Task WhenProjectHasNullFilePath_SkipsSilentlyOnStdout()
    {
        // Arrange
        Solution solution = CreateSolutionWithNullFilePath();
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["list-projects"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        stdout.ToString().ShouldBeEmpty();
        stderr.ToString().TrimEnd().ShouldBe("workspace: complete");
    }

    [Fact]
    public async Task WhenPartialLoad_PrintsDiagnosticsAndPartialSummaryOnStderr()
    {
        // Arrange
        Solution solution = CreateEmptySolution();
        StringWriter stdout = new();
        StringWriter stderr = new();
        LoadHealth health = new(["Project A dropped: missing SDK", "Project B dropped: bad path"]);
        CommandContext context = new(stdout, stderr, solution, health: health);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["list-projects"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        stdout.ToString().ShouldBeEmpty();
        string stderrText = stderr.ToString();
        stderrText.ShouldContain("workspace warning: Project A dropped: missing SDK");
        stderrText.ShouldContain("workspace warning: Project B dropped: bad path");
        stderrText.ShouldContain("workspace: partial - 2 load failures");
        stderrText.ShouldNotContain("results may be incomplete");
    }

    [Fact]
    public async Task WhenPartialLoadAndQuiet_SuppressesDiagnosticsButKeepsSummary()
    {
        // Arrange
        Solution solution = CreateEmptySolution();
        StringWriter stdout = new();
        StringWriter stderr = new();
        LoadHealth health = new(["Project dropped: missing SDK"]);
        CommandContext context = new(stdout, stderr, solution, health: health);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["list-projects", "--quiet"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        stdout.ToString().ShouldBeEmpty();
        string stderrText = stderr.ToString();
        stderrText.ShouldNotContain("workspace warning:");
        stderrText.ShouldContain("workspace: partial - 1 load failure");
    }

    [Fact]
    public async Task WhenSolutionHasProjectsAndCompleteLoad_StdoutByteIdentical()
    {
        // Arrange
        string solutionDir = Path.Combine(Path.GetTempPath(), "mysln-identical");
        string projectPath = Path.Combine(solutionDir, "src", "MyApp", "MyApp.csproj");
        Solution solution = CreateSolutionWithProject("MyApp", projectPath);
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution, solutionDir);

        // Act
        await CommandDispatcher.ExecuteAsync(["list-projects"], context);

        // Assert — stdout contains the project line; stderr carries complete summary only
        string output = stdout.ToString().TrimEnd();
        string expectedRelative = Path.Combine("src", "MyApp", "MyApp.csproj");
        output.ShouldBe($"MyApp\t{expectedRelative}");
        stderr.ToString().TrimEnd().ShouldBe("workspace: complete");
    }

    [Fact]
    public async Task WhenMultipleProjects_OutputsOneLinePerProject()
    {
        // Arrange
        string solutionDir = Path.Combine(Path.GetTempPath(), "mysln");
        string projectPathA = Path.Combine(solutionDir, "A", "A.csproj");
        string projectPathB = Path.Combine(solutionDir, "B", "B.csproj");
        Solution solution = CreateSolutionWithProjects(
            ("ProjectA", projectPathA),
            ("ProjectB", projectPathB));
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution, solutionDir);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["list-projects"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        string[] lines = stdout.ToString().TrimEnd().Split(Environment.NewLine);
        lines.Length.ShouldBe(2);
        lines.ShouldContain($"ProjectA\t{Path.Combine("A", "A.csproj")}");
        lines.ShouldContain($"ProjectB\t{Path.Combine("B", "B.csproj")}");
    }

    private static Solution CreateSolutionWithProject(
        string projectName,
        string projectFilePath)
    {
        AdhocWorkspace workspace = new();
        ProjectInfo projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            projectName,
            projectName,
            LanguageNames.CSharp,
            filePath: projectFilePath,
            metadataReferences:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            ]);
        Project project = workspace.AddProject(projectInfo);
        return project.Solution;
    }

    private static Solution CreateSolutionWithProjects(
        params (string Name, string FilePath)[] projects)
    {
        AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        foreach ((string name, string filePath) in projects)
        {
            ProjectInfo projectInfo = ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Default,
                name,
                name,
                LanguageNames.CSharp,
                filePath: filePath,
                metadataReferences:
                [
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                ]);
            solution = solution.AddProject(projectInfo);
        }
        workspace.TryApplyChanges(solution);
        return workspace.CurrentSolution;
    }

    private static Solution CreateEmptySolution()
    {
        AdhocWorkspace workspace = new();
        return workspace.CurrentSolution;
    }

    private static Solution CreateSolutionWithNullFilePath()
    {
        AdhocWorkspace workspace = new();
        ProjectInfo projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            "InMemoryProject",
            "InMemoryProject",
            LanguageNames.CSharp,
            filePath: null,
            metadataReferences:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            ]);
        Project project = workspace.AddProject(projectInfo);
        return project.Solution;
    }
}
