using System.Collections.Frozen;

using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.TrackedFilesTests;

public sealed class CollectDocumentPaths
{
    [Fact]
    public void WhenSolutionHasNoDocuments_ReturnsEmptySet()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;

        // Act
        FrozenSet<string> paths = TrackedFiles.CollectDocumentPaths(solution);

        // Assert
        paths.ShouldBeEmpty();
    }

    [Fact]
    public void WhenDocumentHasNullFilePath_ExcludesIt()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        ProjectId projectId = ProjectId.CreateNewId();
        DocumentId documentId = DocumentId.CreateNewId(projectId);

        Solution solution = workspace
            .CurrentSolution
            .AddProject(projectId, "Alpha", "Alpha", LanguageNames.CSharp)
            .AddDocument(documentId, "Foo.cs", "class Foo {}", filePath: null);

        // Act
        FrozenSet<string> paths = TrackedFiles.CollectDocumentPaths(solution);

        // Assert
        paths.ShouldBeEmpty();
    }

    [Fact]
    public void ReturnedSet_IsCaseInsensitive()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        ProjectId projectId = ProjectId.CreateNewId();
        DocumentId documentId = DocumentId.CreateNewId(projectId);

        Solution solution = workspace
            .CurrentSolution
            .AddProject(projectId, "Alpha", "Alpha", LanguageNames.CSharp)
            .AddDocument(documentId, "Foo.cs", "class Foo {}", filePath: @"C:\src\Foo.cs");

        // Act
        FrozenSet<string> paths = TrackedFiles.CollectDocumentPaths(solution);

        // Assert
        paths.Contains(@"C:\SRC\FOO.CS").ShouldBeTrue();
    }

    [Fact]
    public void WhenSolutionHasDocuments_ReturnsAllFilePaths()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        ProjectId projectId = ProjectId.CreateNewId();
        DocumentId documentId = DocumentId.CreateNewId(projectId);

        Solution solution = workspace
            .CurrentSolution
            .AddProject(projectId, "Alpha", "Alpha", LanguageNames.CSharp)
            .AddDocument(documentId, "Foo.cs", "class Foo {}", filePath: @"C:\src\Foo.cs");

        // Act
        FrozenSet<string> paths = TrackedFiles.CollectDocumentPaths(solution);

        // Assert
        paths.ShouldContain(@"C:\src\Foo.cs");
    }
}