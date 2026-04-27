using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.CommandDispatcherTests;

public sealed class FindTypeByName
{
    [Fact]
    public async Task WhenTypeIsInMetadata_FindImplSucceeds()
    {
        // Arrange — IDisposable is a metadata interface; source implements it
        string source = @"
using System;

class MyDisposable : IDisposable
{
    public void Dispose() { }
}";
        Solution solution = CreateSolution(source);
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act — find-impl on a metadata interface should find source implementations
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["find-impl", "System.IDisposable"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        stderr.ToString().ShouldNotContain("Type not found");
        stdout.ToString().ShouldContain("MyDisposable");
    }

    [Fact]
    public async Task WhenTypeIsInMetadata_FindBaseSucceeds()
    {
        // Arrange — source class inherits from Exception (a metadata type)
        string source = @"
using System;

class MyException : Exception
{
    public MyException(string message) : base(message) { }
}";
        Solution solution = CreateSolution(source);
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act — find-base on a metadata type should not report "Type not found"
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["find-base", "System.Exception"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        stderr.ToString().ShouldNotContain("Type not found");
    }

    [Fact]
    public async Task WhenTypeIsInMetadata_FindCtorSucceeds()
    {
        // Arrange — source uses ArgumentNullException constructor which is in metadata
        string source = @"
using System;

class Caller
{
    public void Use()
    {
        throw new ArgumentNullException(""param"");
    }
}";
        Solution solution = CreateSolution(source);
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act — find-ctor on a metadata type should not report "Type not found"
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["find-ctor", "System.ArgumentNullException"],
            context);

        // Assert
        exitCode.ShouldBe(0);
        stderr.ToString().ShouldNotContain("Type not found");
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
