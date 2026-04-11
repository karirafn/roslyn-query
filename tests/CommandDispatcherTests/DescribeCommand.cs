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

    [Fact]
    public async Task WhenClassWithBase_OutputsBaseLine()
    {
        // Arrange
        string source = @"
namespace App;
class Animal { }
class Dog : Animal { }";
        Solution solution = CreateSolution(source);
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["describe", "Dog"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        string output = stdout.ToString();
        output.ShouldContain("base:       Animal");
    }

    [Fact]
    public async Task WhenClassWithoutBase_OmitsBaseLine()
    {
        // Arrange
        string source = @"
namespace App;
class Standalone { }";
        Solution solution = CreateSolution(source);
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["describe", "Standalone"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        string[] lines = stdout.ToString().TrimEnd().Split(Environment.NewLine);
        lines.ShouldAllBe(line => !line.StartsWith("base:"));
    }

    [Fact]
    public async Task WhenTypeWithInterfaces_OutputsInterfacesLine()
    {
        // Arrange
        string source = @"
namespace App;
interface IFoo { }
interface IBar { }
class Widget : IFoo, IBar { }";
        Solution solution = CreateSolution(source);
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["describe", "Widget"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        string output = stdout.ToString();
        output.ShouldContain("interfaces: IFoo, IBar");
    }

    [Fact]
    public async Task WhenTypeWithoutInterfaces_OmitsInterfacesLine()
    {
        // Arrange
        string source = @"
namespace App;
class Plain { }";
        Solution solution = CreateSolution(source);
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["describe", "Plain"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        string[] lines = stdout.ToString().TrimEnd().Split(Environment.NewLine);
        lines.ShouldAllBe(line => !line.StartsWith("interfaces:"));
    }

    [Fact]
    public async Task WhenInterface_ShowsExtendedInterfaces()
    {
        // Arrange
        string source = @"
namespace App;
interface IBase { }
interface IChild : IBase { }";
        Solution solution = CreateSolution(source);
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["describe", "IChild"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        string output = stdout.ToString();
        output.ShouldContain("interfaces: IBase");
    }

    [Fact]
    public async Task WhenTypeWithMembers_OutputsMembersLine()
    {
        // Arrange
        string source = @"
namespace App;
class Service
{
    public Service() { }
    public Service(int x) { }
    public string Name { get; set; }
    public int Count { get; }
    public void DoWork() { }
    public int Calculate(int x) { return x; }
    public void Reset() { }
}";
        Solution solution = CreateSolution(source);
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["describe", "Service"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        string output = stdout.ToString();
        output.ShouldContain("members:    2 ctors, 2 props, 3 methods");
    }

    [Fact]
    public async Task WhenTypeWithNoMembers_OmitsMembersLine()
    {
        // Arrange
        string source = @"
namespace App;
interface IEmpty { }";
        Solution solution = CreateSolution(source);
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["describe", "IEmpty"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        string[] lines = stdout.ToString().TrimEnd().Split(Environment.NewLine);
        lines.ShouldAllBe(line => !line.StartsWith("members:"));
    }

    [Fact]
    public async Task WhenEnum_OutputsFieldCount()
    {
        // Arrange
        string source = @"
namespace App;
enum Color { Red, Green, Blue }";
        Solution solution = CreateSolution(source);
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["describe", "Color"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        string output = stdout.ToString();
        output.ShouldContain("members:    3 fields");
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
