using System.Collections.Frozen;

using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ReloadStateTests;

public sealed class Snapshot
{
    [Fact]
    public void WhenSnapshotCalled_ReturnsSolutionDocumentPathsAndHealthConsistentWithLastCompleteInitialLoad()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        List<string> documentPathList = [@"C:\src\Foo.cs"];
        FrozenSet<string> documentPaths = documentPathList.ToFrozenSet();
        LoadHealth health = new(["Alpha failed"]);
        ReloadState sut = new(solution, []);

        sut.CompleteInitialLoad(solution, [], documentPaths, health);

        // Act
        (Solution snapshotSolution, FrozenSet<string> snapshotDocumentPaths, LoadHealth snapshotHealth) =
            sut.Snapshot();

        // Assert
        snapshotSolution.ShouldBeSameAs(solution);
        snapshotDocumentPaths.ShouldBeSameAs(documentPaths);
        snapshotHealth.ShouldBeSameAs(health);
    }

    [Fact]
    public void WhenSnapshotCalled_ReturnsSolutionDocumentPathsAndHealthConsistentWithLastCompleteReload()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        List<string> documentPathList = [@"C:\src\Bar.cs"];
        FrozenSet<string> documentPaths = documentPathList.ToFrozenSet();
        LoadHealth health = new(["Beta failed"]);
        ReloadState sut = new(solution, []);

        sut.TryBeginReload().ShouldBeTrue();
        sut.CompleteReload(solution, [], documentPaths, health);

        // Act
        (Solution snapshotSolution, FrozenSet<string> snapshotDocumentPaths, LoadHealth snapshotHealth) =
            sut.Snapshot();

        // Assert
        snapshotSolution.ShouldBeSameAs(solution);
        snapshotDocumentPaths.ShouldBeSameAs(documentPaths);
        snapshotHealth.ShouldBeSameAs(health);
    }
}
