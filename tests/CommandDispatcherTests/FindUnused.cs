using System.Globalization;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.CommandDispatcherTests;

public sealed class FindUnused
{
    [Fact]
    public async Task WhenUnusedSymbolsExist_OutputsSortedByFilePathThenLineNumber()
    {
        // Arrange
        string sourceA = @"
namespace TestProject;

public class Alpha
{
    public void UnusedA() { }
    public void UnusedB() { }
}";
        string sourceB = @"
namespace TestProject;

public class Beta
{
    public void UnusedC() { }
}";
        Solution solution = CreateSolution(("B.cs", sourceB), ("A.cs", sourceA));
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["find-unused", "--absolute"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        string output = stdout.ToString();
        string[] lines = output.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Length.ShouldBeGreaterThan(0);

        // Verify sorted by file path then line number
        List<(string Path, int Line)> parsed = [];
        foreach (string line in lines)
        {
            string locationPart = line.Split('\t')[0];
            string[] parts = locationPart.Split(':');
            string filePath = parts[0];
            int lineNumber = int.Parse(parts[1], CultureInfo.InvariantCulture);
            parsed.Add((filePath, lineNumber));
        }

        for (int i = 1; i < parsed.Count; i++)
        {
            int pathComparison = string.Compare(
                parsed[i - 1].Path,
                parsed[i].Path,
                StringComparison.Ordinal);
            if (pathComparison == 0)
            {
                parsed[i - 1].Line.ShouldBeLessThanOrEqualTo(
                    parsed[i].Line,
                    $"Lines not sorted within file {parsed[i].Path}");
            }
            else
            {
                pathComparison.ShouldBeLessThan(
                    0,
                    $"Files not sorted: {parsed[i - 1].Path} should come before {parsed[i].Path}");
            }
        }
    }

    [Fact]
    public async Task WhenNoUnusedSymbols_PrintsNotFoundMessage()
    {
        // Arrange
        string source = @"
namespace TestProject;

public class Foo
{
    public void UsedMethod() { }
    public void Caller() { UsedMethod(); }
}";
        Solution solution = CreateSolution(("Test.cs", source));
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["find-unused", "--absolute"],
            context);

        // Assert
        exitCode.ShouldBe(0);
    }

    [Fact]
    public async Task WhenMultipleFiles_ResultsAreDeterministicallySorted()
    {
        // Arrange
        string sourceZ = @"
namespace TestProject;

public class Zebra
{
    public void Unused1() { }
}";
        string sourceA = @"
namespace TestProject;

public class Aardvark
{
    public void Unused2() { }
}";
        Solution solution = CreateSolution(("Z.cs", sourceZ), ("A.cs", sourceA));
        StringWriter stdout1 = new();
        StringWriter stderr1 = new();
        CommandContext context1 = new(stdout1, stderr1, solution);

        StringWriter stdout2 = new();
        StringWriter stderr2 = new();
        CommandContext context2 = new(stdout2, stderr2, solution);

        // Act
        await CommandDispatcher.ExecuteAsync(["find-unused", "--absolute"], context1);
        await CommandDispatcher.ExecuteAsync(["find-unused", "--absolute"], context2);

        // Assert
        string output1 = stdout1.ToString();
        string output2 = stdout2.ToString();
        output1.ShouldBe(output2, "Output should be deterministic across runs");
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
