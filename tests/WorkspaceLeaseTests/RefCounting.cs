using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.WorkspaceLeaseTests;

public sealed class RefCounting
{
    // Tracks disposal calls — a real MSBuildWorkspace is not needed in unit tests.
    private sealed class TrackingDisposable : IDisposable
    {
        public int DisposeCount;
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    [Fact]
    public void WhenRetiredWithNoExtraRefs_DisposesOnce()
    {
        // Arrange
        TrackingDisposable target = new();
        WorkspaceLease sut = new(target);

        // Act
        sut.Retire();

        // Assert
        target.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public void WhenReleaseCalledBeforeRetire_DoesNotDispose()
    {
        // Arrange
        TrackingDisposable target = new();
        WorkspaceLease sut = new(target);
        sut.AddRef();

        // Act
        sut.Release(); // releases extra ref; daemon's implicit ref still held

        // Assert
        target.DisposeCount.ShouldBe(0);
    }

    [Fact]
    public void WhenAddRefThenRetireThenRelease_DisposesOnce()
    {
        // Arrange
        TrackingDisposable target = new();
        WorkspaceLease sut = new(target);
        sut.AddRef();   // in-flight query holds a ref

        // Act
        sut.Retire();   // daemon supersedes the lease; count goes to 1 (the query's ref)
        sut.Release();  // query releases; count reaches 0 — dispose fires

        // Assert
        target.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public void WhenRetiredTwice_DisposesExactlyOnce()
    {
        // Arrange
        TrackingDisposable target = new();
        WorkspaceLease sut = new(target);

        // Act
        sut.Retire();
        sut.Retire(); // second retire is a no-op

        // Assert
        target.DisposeCount.ShouldBe(1);
    }

    [Fact]
    public void WhenReleaseCalledMoreTimesThanAddRef_NeverDisposesBeforeRetired()
    {
        // Arrange
        TrackingDisposable target = new();
        WorkspaceLease sut = new(target);
        sut.AddRef();

        // Act — Release twice without a matching second AddRef
        sut.Release();
        sut.Release(); // double-release; must not go below 0 or trigger dispose

        // Assert — retired is still false, so no dispose
        target.DisposeCount.ShouldBe(0);
    }

    [Fact]
    public void WhenAddRefRacesWithRetire_DisposalDeferredUntilThatRefReleases()
    {
        // Arrange — simulates a query thread calling AddRef concurrently with Retire.
        // In practice the query thread acquires a ref from the snapshot before the daemon
        // can retire the lease; the test exercises the resulting state (count=2 with retired
        // bit set) to verify that Release — not Retire — triggers disposal.
        TrackingDisposable target = new();
        WorkspaceLease sut = new(target);
        sut.AddRef(); // query thread acquired a ref; count = 2

        // Act — daemon retires; count drops from 2 to 1 (retired bit set, but count > 0)
        sut.Retire();

        // Assert — the query ref is still held; no dispose yet
        target.DisposeCount.ShouldBe(0);

        // Act — query releases its ref; count reaches 0 AND retired → dispose fires
        sut.Release();

        // Assert
        target.DisposeCount.ShouldBe(1);
    }
}
