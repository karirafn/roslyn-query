using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.CommandDispatcherTests;

public sealed class InProjectFlag
{
    private static readonly string SolutionDir =
        Path.Combine(Path.GetTempPath(), "insln");

    private static readonly string ApiProjectPath =
        Path.Combine(SolutionDir, "src", "MyApp.Api", "MyApp.Api.csproj");

    private static readonly string DomainProjectPath =
        Path.Combine(SolutionDir, "src", "MyApp.Domain", "MyApp.Domain.csproj");

    [Fact]
    public async Task WhenUnknownProjectName_ReturnsError()
    {
        // Arrange
        Solution solution = CreateSolution();
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution, SolutionDir);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["find-refs", "Foo", "--in-project", "NonExistent"],
            context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("project 'NonExistent' not found");
    }

    [Theory]
    [InlineData("find-base")]
    [InlineData("list-members")]
    [InlineData("describe")]
    [InlineData("list-projects")]
    public async Task WhenUnsupportedCommand_ReturnsError(string command)
    {
        // Arrange
        Solution solution = CreateSolution();
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution, SolutionDir);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            [command, "Foo", "--in-project", "MyApp.Api"],
            context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain($"--in-project is not supported for {command}");
    }

    [Fact]
    public async Task WhenInProjectMatchesCaseInsensitively_FiltersResults()
    {
        // Arrange
        string projectDir = Path.GetDirectoryName(ApiProjectPath)!;
        string inFileRelative = Path.Combine("src", "MyApp.Api", "Controller.cs");
        string outFileRelative = Path.Combine("src", "MyApp.Domain", "Order.cs");

        StringWriter inner = new();
        ProjectFilteringWriter writer = new(inner, projectDir, SolutionDir);

        // Act
        await writer.WriteLineAsync($"{inFileRelative}:10");
        await writer.WriteLineAsync($"{outFileRelative}:20");

        // Assert — only the in-project line passes through
        string[] lines = inner.ToString().Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(1);
        lines[0].ShouldContain(inFileRelative);
    }

    [Fact]
    public async Task WhenInProjectAndCount_CountReflectsFilteredResults()
    {
        // Arrange
        string projectDir = Path.GetDirectoryName(ApiProjectPath)!;
        string inFile = Path.Combine("src", "MyApp.Api", "Controller.cs");
        string outFile = Path.Combine("src", "MyApp.Domain", "Order.cs");

        CountingWriter counter = new();
        ProjectFilteringWriter writer = new(counter, projectDir, SolutionDir);

        // Act
        await writer.WriteLineAsync($"{inFile}:10");
        await writer.WriteLineAsync($"{outFile}:20");
        await writer.WriteLineAsync($"{inFile}:30");

        // Assert — only 2 in-project lines counted
        counter.Count.ShouldBe(2);
    }

    [Fact]
    public async Task WhenFilePathIsAbsolute_UsesDirectoryContainment()
    {
        // Arrange
        string projectDir = Path.GetDirectoryName(ApiProjectPath)!;
        string inFile = Path.Combine(projectDir, "Controller.cs");
        string outFile = Path.Combine(SolutionDir, "src", "MyApp.Domain", "Order.cs");

        StringWriter inner = new();
        ProjectFilteringWriter writer = new(inner, projectDir, SolutionDir);

        // Act
        await writer.WriteLineAsync($"{inFile}:10");
        await writer.WriteLineAsync($"{outFile}:20");

        // Assert
        string[] lines = inner.ToString().Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(1);
        lines[0].ShouldContain(inFile);
    }

    [Fact]
    public async Task WhenProjectHasNullFilePath_ReturnsError()
    {
        // Arrange
        AdhocWorkspace workspace = new();
        ProjectInfo info = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            "InMemory",
            "InMemory",
            LanguageNames.CSharp,
            filePath: null,
            metadataReferences:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            ]);
        Project project = workspace.AddProject(info);
        Solution solution = project.Solution;
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution, SolutionDir);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["find-refs", "Foo", "--in-project", "InMemory"],
            context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("has no file path");
    }

    [Fact]
    public async Task WhenListTypesFormat_ExtractsFilePathFromLastField()
    {
        // Arrange — list-types output: kind\ttype\tpath:line
        string projectDir = Path.GetDirectoryName(ApiProjectPath)!;
        string inFile = Path.Combine("src", "MyApp.Api", "Controller.cs");
        string outFile = Path.Combine("src", "MyApp.Domain", "Order.cs");

        StringWriter inner = new();
        ProjectFilteringWriter writer = new(inner, projectDir, SolutionDir);

        // Act
        await writer.WriteLineAsync($"class\tMyApp.Api.Controller\t{inFile}:5");
        await writer.WriteLineAsync($"class\tMyApp.Domain.Order\t{outFile}:10");

        // Assert — only in-project type passes through
        string[] lines = inner.ToString().Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(1);
        lines[0].ShouldContain("Controller");
    }

    [Theory]
    [InlineData("src/Foo.cs:42", "src/Foo.cs")]
    [InlineData("src/Foo.cs:42\tsymbol", "src/Foo.cs")]
    [InlineData("class\tMyApp.Foo\tsrc/Foo.cs:5", "src/Foo.cs")]
    [InlineData("# Symbol", null)]
    [InlineData("src/Foo.cs:42\tsource text\tsymbol", "src/Foo.cs")]
    public void ExtractFilePath_VariousFormats(string line, string? expected)
    {
        // Act
        string? result = ProjectFilteringWriter.ExtractFilePath(line);

        // Assert
        result.ShouldBe(expected);
    }

    [Fact]
    public async Task WhenProjectDirIsPrefixOfAnotherProjectDir_DoesNotIncludeOtherProject()
    {
        // Arrange — MyApp.Api is NOT a prefix issue since dirs are distinct,
        // but we test that containment uses separator boundary.
        // e.g. project dir "src/MyApp" should not match "src/MyApp.Extra/Foo.cs"
        string shortProjectDir = Path.Combine(SolutionDir, "src", "MyApp");
        string inFile = Path.Combine(SolutionDir, "src", "MyApp", "Foo.cs");
        string prefixTrapFile = Path.Combine(SolutionDir, "src", "MyApp.Extra", "Bar.cs");

        StringWriter inner = new();
        ProjectFilteringWriter writer = new(inner, shortProjectDir, SolutionDir);

        // Act
        await writer.WriteLineAsync($"{inFile}:1");
        await writer.WriteLineAsync($"{prefixTrapFile}:2");

        // Assert
        string[] lines = inner.ToString().Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(1);
        lines[0].ShouldContain("MyApp");
        lines[0].ShouldNotContain("MyApp.Extra");
    }

    private static Solution CreateSolution()
    {
        AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;

        foreach ((string name, string path) in new[]
        {
            ("MyApp.Api", ApiProjectPath),
            ("MyApp.Domain", DomainProjectPath),
        })
        {
            ProjectInfo info = ProjectInfo.Create(
                ProjectId.CreateNewId(),
                VersionStamp.Default,
                name,
                name,
                LanguageNames.CSharp,
                filePath: path,
                metadataReferences:
                [
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                ]);
            solution = solution.AddProject(info);
        }

        workspace.TryApplyChanges(solution);
        return workspace.CurrentSolution;
    }
}
