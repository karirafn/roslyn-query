using System.Collections.Frozen;

using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ReloadStateTests;

public sealed class DocumentPaths
{
    [Fact]
    public void WhenConstructedWithoutDocumentPaths_DocumentPathsIsEmpty()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;

        // Act
        ReloadState sut = new(solution, []);

        // Assert
        sut.DocumentPaths.ShouldBeEmpty();
    }

    [Fact]
    public void WhenConstructedWithDocumentPaths_ExposesProvidedSet()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        List<string> pathList = [@"C:\src\Foo.cs"];
        FrozenSet<string> documentPaths = pathList.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        // Act
        ReloadState sut = new(solution, [], documentPaths);

        // Assert
        sut.DocumentPaths.ShouldBeSameAs(documentPaths);
    }
}