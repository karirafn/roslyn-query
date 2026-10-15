using System.Collections.Frozen;

using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ReloadStateTests;

public sealed class Snapshot
{
    private sealed class TrackingDisposable : IDisposable
    {
        public int DisposeCount;
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    [Fact]
    public void WhenSnapshotCalled_ReturnsSolutionDocumentPathsAndHealthConsistentWithLastCompleteInitialLoad()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        List<string> documentPathList = [@"C:\src\Foo.cs"];
        FrozenSet<string> documentPaths = documentPathList.ToFrozenSet();
        LoadHealth health = new(["Alpha failed"]);
        WorkspaceLease lease = new(new TrackingDisposable());
        ReloadState sut = new(solution, []);

        sut.CompleteInitialLoad(solution, [], documentPaths, health, lease);

        // Act
        (Solution snapshotSolution, FrozenSet<string> snapshotDocumentPaths, LoadHealth snapshotHealth, WorkspaceLease snapshotLease) =
            sut.Snapshot();

        // Assert
        try
        {
            snapshotSolution.ShouldBeSameAs(solution);
            snapshotDocumentPaths.ShouldBeSameAs(documentPaths);
            snapshotHealth.ShouldBeSameAs(health);
            snapshotLease.ShouldBeSameAs(lease);
        }
        finally
        {
            snapshotLease.Release();
        }
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
        WorkspaceLease initialLease = new(new TrackingDisposable());
        WorkspaceLease reloadLease = new(new TrackingDisposable());
        ReloadState sut = new(solution, []);

        sut.CompleteInitialLoad(solution, [], FrozenSet<string>.Empty, LoadHealth.Complete, initialLease);
        sut.TryBeginReload().ShouldBeTrue();
        sut.CompleteReload(solution, [], documentPaths, health, reloadLease);

        // Act
        (Solution snapshotSolution, FrozenSet<string> snapshotDocumentPaths, LoadHealth snapshotHealth, WorkspaceLease snapshotLease) =
            sut.Snapshot();

        // Assert
        try
        {
            snapshotSolution.ShouldBeSameAs(solution);
            snapshotDocumentPaths.ShouldBeSameAs(documentPaths);
            snapshotHealth.ShouldBeSameAs(health);
            snapshotLease.ShouldBeSameAs(reloadLease);
        }
        finally
        {
            snapshotLease.Release();
        }
    }

    [Fact]
    public void WhenSnapshotTaken_AddRefIsCalledOnLease()
    {
        // Arrange — verify Snapshot calls AddRef (so callers must Release)
        TrackingDisposable target = new();
        WorkspaceLease lease = new(target);
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        ReloadState sut = new(solution, []);
        sut.CompleteInitialLoad(solution, [], FrozenSet<string>.Empty, LoadHealth.Complete, lease);

        // Act
        (_, _, _, WorkspaceLease snapshotLease) = sut.Snapshot();

        // Assert — lease not yet disposed; snapshot holds a ref, daemon's implicit ref still held
        target.DisposeCount.ShouldBe(0);

        // Cleanup
        snapshotLease.Release();
    }

    [Fact]
    public void WhenSnapshotReleasedAndLeaseRetired_DisposesWorkspace()
    {
        // Arrange
        TrackingDisposable target = new();
        WorkspaceLease lease = new(target);
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        ReloadState sut = new(solution, []);
        sut.CompleteInitialLoad(solution, [], FrozenSet<string>.Empty, LoadHealth.Complete, lease);

        (_, _, _, WorkspaceLease snapshotLease) = sut.Snapshot();

        // Act — retire the old lease (simulating what CompleteReload does), then release snapshot ref
        lease.Retire();
        snapshotLease.Release();

        // Assert — both the daemon's implicit ref (via Retire) and the snapshot ref are gone
        target.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public void WhenCompleteReloadSwapsLease_OldLeaseRetiredUnderLock()
    {
        // Arrange — old lease must not dispose until after snapshot ref is released
        TrackingDisposable oldTarget = new();
        WorkspaceLease oldLease = new(oldTarget);
        TrackingDisposable newTarget = new();
        WorkspaceLease newLease = new(newTarget);

        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        ReloadState sut = new(solution, []);
        sut.CompleteInitialLoad(solution, [], FrozenSet<string>.Empty, LoadHealth.Complete, oldLease);

        // Take a snapshot ref — this must AddRef old lease under the same lock that CompleteReload will retire it
        (_, _, _, WorkspaceLease snapshotLease) = sut.Snapshot();

        // Act — CompleteReload retires oldLease inside the lock, swaps in newLease
        sut.TryBeginReload().ShouldBeTrue();
        sut.CompleteReload(solution, [], FrozenSet<string>.Empty, LoadHealth.Complete, newLease);

        // Assert — old lease retired but snapshot ref still held; no dispose yet
        oldTarget.DisposeCount.ShouldBe(0);

        // Release snapshot ref — now old workspace disposes
        snapshotLease.Release();
        oldTarget.DisposeCount.ShouldBe(1);

        // New lease is unretired (daemon still holds it)
        newTarget.DisposeCount.ShouldBe(0);

        // Cleanup — retire new lease
        newLease.Retire();
        newTarget.DisposeCount.ShouldBe(1);
    }
}
