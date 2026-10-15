using System.Collections.Frozen;

using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ReloadStateTests;

/// <summary>
/// Verifies that repeated AbortReload calls introduce an increasing cooldown before
/// ClassifyChangeAsync returns Full again, and that a genuine structural-dirty signal
/// bypasses the cooldown entirely.
/// </summary>
public sealed class AbortReloadBackoff
{
    private sealed class TrackingDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private static WorkspaceLease NewLease() => new(new TrackingDisposable());

    [Fact]
    public async Task WhenAbortedOnce_ClassifyReturnsFull_ImmediatelyAsync()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        ReloadState sut = new(solution, []);

        sut.TryBeginReload().ShouldBeTrue();
        sut.AbortReload();

        // Act — classify immediately; cooldown should not apply after first abort
        StalenessVerdict result = await sut.ClassifyChangeAsync(structuralDirty: false);

        // Assert — first abort has no cooldown delay
        result.ShouldBe(StalenessVerdict.Full);
    }

    [Fact]
    public async Task WhenAbortedTwice_SecondClassifyReturnsFull_AfterCooldownAsync()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        ReloadState sut = new(solution, []);

        // First abort — no cooldown
        sut.TryBeginReload().ShouldBeTrue();
        sut.AbortReload();
        StalenessVerdict first = await sut.ClassifyChangeAsync(structuralDirty: false);
        first.ShouldBe(StalenessVerdict.Full);

        // Second abort — triggers backoff; classify immediately returns None (cooldown not elapsed)
        sut.TryBeginReload().ShouldBeTrue();
        sut.AbortReload();

        // Act — classify immediately; cooldown should suppress Full
        StalenessVerdict gateClosed = await sut.ClassifyChangeAsync(structuralDirty: false);

        // Assert — second abort has a cooldown; Full is suppressed until cooldown elapses
        gateClosed.ShouldBe(StalenessVerdict.None);
    }

    [Fact]
    public async Task WhenCooldownElapsed_ClassifyReturnsFull_AgainAsync()
    {
        // Arrange — use a test seam to inject a past retry time
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        ReloadState sut = new(solution, []);

        // First abort — no cooldown
        sut.TryBeginReload().ShouldBeTrue();
        sut.AbortReload();
        await sut.ClassifyChangeAsync(structuralDirty: false);

        // Second abort sets the cooldown; use the seam to bypass it
        sut.TryBeginReload().ShouldBeTrue();
        sut.AbortReload();

        // Override the next retry time to a past timestamp so the cooldown appears elapsed
        sut.SetNextRetryAllowedAtForTests(DateTime.UtcNow - TimeSpan.FromSeconds(1));

        // Act — classify after the simulated elapsed cooldown
        StalenessVerdict result = await sut.ClassifyChangeAsync(structuralDirty: false);

        // Assert — cooldown elapsed; Full returns again
        result.ShouldBe(StalenessVerdict.Full);
    }

    [Fact]
    public async Task WhenStructuralDirtyDuringCooldown_BypassesCooldownAndReturnsFull_Async()
    {
        // Arrange — structural-dirty signal (watcher event) must never be suppressed by backoff
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        ReloadState sut = new(solution, []);

        // First abort — no cooldown
        sut.TryBeginReload().ShouldBeTrue();
        sut.AbortReload();
        await sut.ClassifyChangeAsync(structuralDirty: false);

        // Second abort — starts cooldown
        sut.TryBeginReload().ShouldBeTrue();
        sut.AbortReload();

        // Act — a genuine watcher event arrives; must bypass cooldown
        StalenessVerdict result = await sut.ClassifyChangeAsync(structuralDirty: true);

        // Assert — genuine structural change always wins
        result.ShouldBe(StalenessVerdict.Full);
    }

    [Fact]
    public async Task WhenCompleteReloadFollowsMultipleAborts_BackoffReset_ClassifyReturnsNone_Async()
    {
        // Arrange — a successful reload must reset the abort counter and backoff
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        ReloadState sut = new(solution, []);

        // First abort — no cooldown
        sut.TryBeginReload().ShouldBeTrue();
        sut.AbortReload();
        await sut.ClassifyChangeAsync(structuralDirty: false);

        // Second abort — sets cooldown
        sut.TryBeginReload().ShouldBeTrue();
        sut.AbortReload();

        // Successful reload resets everything
        sut.TryBeginReload().ShouldBeTrue();
        sut.CompleteReload(solution, [], FrozenSet<string>.Empty, LoadHealth.Complete, NewLease());

        // Act — after successful reload, no files changed — classify should return None
        StalenessVerdict result = await sut.ClassifyChangeAsync(structuralDirty: false);

        // Assert — backoff reset; with no file changes result is None
        result.ShouldBe(StalenessVerdict.None);
    }
}
