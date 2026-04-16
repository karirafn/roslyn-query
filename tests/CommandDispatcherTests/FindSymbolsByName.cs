using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.CommandDispatcherTests;

public sealed class FindSymbolsByName
{
    [Fact]
    public async Task WhenMethodHasQualifiedReturnType_FindsByFqn()
    {
        // Arrange
        string source = @"
using System.Collections.Generic;

namespace MyApp.Services
{
    public class OrderService
    {
        public List<int> GetItems() { return new List<int>(); }
    }

    public class Consumer
    {
        public void Use()
        {
            var svc = new OrderService();
            svc.GetItems();
        }
    }
}";
        Solution solution = CreateSolution(source);
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["find-refs", "MyApp.Services.OrderService.GetItems"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        stdout.ToString().ShouldNotBeEmpty();
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
