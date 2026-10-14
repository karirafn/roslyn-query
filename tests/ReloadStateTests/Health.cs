using System.Collections.Frozen;

using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ReloadStateTests;

public sealed class Health
{
    [Fact]
    public void WhenConstructedWithoutHealth_ExposesComplete()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;

        // Act
        ReloadState sut = new(solution, []);

        // Assert
        sut.Health.ShouldBeSameAs(LoadHealth.Complete);
    }

    [Fact]
    public void WhenCompleteInitialLoadCalledWithHealth_ExposesProvidedHealth()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        ReloadState sut = new(solution, []);
        LoadHealth partialHealth = new(["project Alpha failed to load"]);

        // Act
        sut.CompleteInitialLoad(solution, [], FrozenSet<string>.Empty, partialHealth);

        // Assert
        sut.Health.ShouldBeSameAs(partialHealth);
    }

    [Fact]
    public void WhenCompleteReloadCalledWithDifferentHealth_ReplacesHealth()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        LoadHealth initialHealth = new(["Alpha failed"]);
        ReloadState sut = new(solution, [], health: initialHealth);
        LoadHealth reloadHealth = new(["Beta failed", "Gamma failed"]);

        // Act
        sut.TryBeginReload().ShouldBeTrue();
        sut.CompleteReload(solution, [], FrozenSet<string>.Empty, reloadHealth);

        // Assert
        sut.Health.ShouldBeSameAs(reloadHealth);
    }

    [Fact]
    public void WhenAbortReloadCalled_LeavesHealthUnchanged()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        LoadHealth snapshotHealth = new(["Delta failed"]);
        ReloadState sut = new(solution, [], health: snapshotHealth);

        // Act
        sut.TryBeginReload().ShouldBeTrue();
        sut.AbortReload();

        // Assert
        sut.Health.ShouldBeSameAs(snapshotHealth);
    }
}
