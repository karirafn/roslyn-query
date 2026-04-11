using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.CommandDispatcherTests;

public sealed class DescribeCommand
{
    [Fact]
    public async Task WhenTypeNotFound_ReturnsError()
    {
        // Arrange
        string source = @"
namespace TestApp;
class Foo { }";
        Solution solution = CreateSolution(source);
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["describe", "NonExistentXyz"],
            context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("Type not found");
    }

    [Fact]
    public async Task WhenAmbiguousType_ReturnsDisambiguationHint()
    {
        // Arrange
        string source = @"
namespace Alpha
{
    class Widget { }
}
namespace Beta
{
    class Widget { }
}";
        Solution solution = CreateSolution(source);
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["describe", "Widget"],
            context);

        // Assert
        exitCode.ShouldBe(1);
        string error = stderr.ToString();
        error.ShouldContain("Alpha.Widget");
        error.ShouldContain("Beta.Widget");
    }

    [Fact]
    public async Task WhenClassType_OutputsHeaderLine()
    {
        // Arrange
        string source = @"
namespace App;
class MyService { }";
        Solution solution = CreateSolution(source);
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["describe", "MyService"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        string[] lines = stdout.ToString().TrimEnd().Split(Environment.NewLine);
        lines[0].ShouldBe("class App.MyService  Test.cs:3");
    }

    private static Solution CreateSolution(string source)
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
        Document document = workspace.AddDocument(
            project.Id,
            "Test.cs",
            SourceText.From(source));
        return document.Project.Solution;
    }
}
