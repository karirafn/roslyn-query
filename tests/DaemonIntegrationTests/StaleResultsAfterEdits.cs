using Shouldly;

namespace roslyn_query.Tests.DaemonIntegrationTests;

/// <summary>
/// End-to-end integration tests for issue #87: a warm daemon must reflect source changes
/// without a restart, both for in-place edits (incremental path) and file additions/removals
/// (full-reload path via WorkspaceWatcher).
///
/// Design constraints (per the fix):
/// - The staleness check only runs when a request arrives AND ≥2 s have elapsed since the
///   last check (StalenessCheckInterval = 2 s). Each poll may advance the throttle clock and
///   trigger detection. Tests therefore poll continuously until the result reflects the change
///   or a 120 s deadline expires.
/// - Assert on find-refs stdout only. A reload-complete banner on stderr can print while the
///   served solution is still stale, so asserting the banner would pass on the unfixed code.
/// - A Reloading/Loading response means the daemon is in the middle of a reload — continue
///   polling; do not fail.
/// </summary>
public sealed class StaleResultsAfterEdits : IAsyncLifetime
{
    private const int DeadlineSeconds = 120;

    // Polling interval between find-refs queries. Must be long enough to let the daemon's
    // 2 s staleness throttle elapse and a reload complete between polls.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    // Symbol FQN: namespace MinimalLib, class A, method M.
    private const string TargetSymbolFqn = "MinimalLib.A.M";

    private readonly DaemonTestFixture _fixture = new("stale-edits");

    public async Task InitializeAsync()
    {
        if (_fixture.Skip)
        {
            return;
        }

        _fixture.CopyFixture();

        // Ensure cold start: stop any stale daemon for this fixture path.
        await _fixture.StopDaemonAsync();
    }

    public async Task DisposeAsync()
    {
        if (_fixture.Skip)
        {
            return;
        }

        await _fixture.StopDaemonAsync();
        _fixture.Cleanup();
    }

    /// <summary>
    /// Scenario (a) — Incremental (edit of tracked .cs):
    ///
    /// The daemon loads the fixture with Caller.Invoke() having an empty body — so A.M has
    /// no callers. We then edit Caller.cs to call _target.M() inside Invoke(). The incremental
    /// path (WithDocumentText) must reflect this edit so that the next find-refs returns the
    /// Caller.cs reference.
    /// </summary>
    [Fact]
    public async Task WhenTrackedFileEdited_IncrementalPathReflectsNewCaller()
    {
        // Arrange
        if (_fixture.Skip)
        {
            // Graceful skip when binary absent — consistent with ADR 0002.
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(DeadlineSeconds));

        // Warm the daemon: first query loads and caches the workspace.
        int warmupExit = await _fixture.RunClientAsync(
            ["list-projects", _fixture.FixtureSlnxPath], cts.Token);
        warmupExit.ShouldBe(0, "warm-up should succeed before edit");

        // Confirm baseline: A.M has no callers initially.
        (string baseStdout, _, int baseExit) = await _fixture.RunClientWithOutputAsync(
            ["find-refs", TargetSymbolFqn, _fixture.FixtureSlnxPath], cts.Token);
        baseExit.ShouldBe(0, "baseline find-refs should succeed");
        baseStdout.Contains("Caller.cs", StringComparison.Ordinal).ShouldBeFalse(
            "baseline: Caller.cs should not appear before the edit");

        // Act — edit Caller.cs so Invoke() calls _target.M().
        string callerPath = Path.Combine(_fixture.FixtureDir, "MinimalLib", "Caller.cs");
        await File.WriteAllTextAsync(
            callerPath,
            """
            namespace MinimalLib;

            public sealed class Caller
            {
                private readonly A _target = new();

                public void Invoke() { _target.M(); }
            }
            """,
            cts.Token);

        // Poll find-refs until Caller.cs appears in stdout, or the deadline expires.
        // The 2 s staleness throttle requires at least one request after 2 s; we space polls
        // at PollInterval (1 s) so the throttle elapses across two consecutive polls.
        string? finalStdout = await PollUntilStdoutContainsAsync(
            "Caller.cs",
            cts.Token);

        // Assert — the incremental WithDocumentText path must have updated the solution.
        finalStdout.ShouldNotBeNull(
            $"find-refs for {TargetSymbolFqn} should have returned Caller.cs within {DeadlineSeconds}s " +
            "after the in-place edit — the incremental update path did not reflect the change.");
        finalStdout.ShouldContain(
            "Caller.cs",
            Case.Sensitive,
            $"stdout should reference Caller.cs after the edit; actual stdout: {finalStdout}");
    }

    /// <summary>
    /// Scenario (b) — Full reload (file added):
    ///
    /// A new file Caller2.cs is dropped into the fixture after the daemon is warm. The watcher
    /// must set the dirty flag; the next request triggers a full MSBuild reload; the subsequent
    /// find-refs must return Caller2.cs as a caller of A.M.
    /// </summary>
    [Fact]
    public async Task WhenNewFileAdded_FullReloadPicksUpNewCaller()
    {
        // Arrange
        if (_fixture.Skip)
        {
            // Graceful skip when binary absent — consistent with ADR 0002.
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(DeadlineSeconds));

        // Warm the daemon.
        int warmupExit = await _fixture.RunClientAsync(
            ["list-projects", _fixture.FixtureSlnxPath], cts.Token);
        warmupExit.ShouldBe(0, "warm-up should succeed before file addition");

        // Act — add Caller2.cs with a class whose method calls A.M().
        string caller2Path = Path.Combine(_fixture.FixtureDir, "MinimalLib", "Caller2.cs");
        await File.WriteAllTextAsync(
            caller2Path,
            """
            namespace MinimalLib;

            public sealed class Caller2
            {
                private readonly A _target = new();

                public void Invoke() { _target.M(); }
            }
            """,
            cts.Token);

        // Poll find-refs until Caller2.cs appears in stdout.
        string? finalStdout = await PollUntilStdoutContainsAsync(
            "Caller2.cs",
            cts.Token);

        // Assert — the watcher-triggered full reload must have loaded the new file.
        finalStdout.ShouldNotBeNull(
            $"find-refs for {TargetSymbolFqn} should have returned Caller2.cs within {DeadlineSeconds}s " +
            "after the file addition — the watcher-triggered full reload did not pick up the new file.");
        finalStdout.ShouldContain(
            "Caller2.cs",
            Case.Sensitive,
            $"stdout should reference Caller2.cs after addition; actual stdout: {finalStdout}");
    }

    /// <summary>
    /// Scenario (c) — Full reload (removal / the exact bug reported in issue #87):
    ///
    /// After establishing that A.M has a caller (Caller2.cs from scenario b, or by adding it
    /// here), we remove that file. The watcher triggers a full reload; the subsequent find-refs
    /// must NOT return Caller2.cs. This is the issue's repro: the daemon reported "reloading"
    /// then continued to serve stale results. This test is the guard that the removed reference
    /// is gone after the reload completes.
    /// </summary>
    [Fact]
    public async Task WhenCallerFileRemoved_FullReloadEliminatesStaleReference()
    {
        // Arrange
        if (_fixture.Skip)
        {
            // Graceful skip when binary absent — consistent with ADR 0002.
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(DeadlineSeconds));

        // Warm the daemon.
        int warmupExit = await _fixture.RunClientAsync(
            ["list-projects", _fixture.FixtureSlnxPath], cts.Token);
        warmupExit.ShouldBe(0, "warm-up should succeed before establishing caller");

        // Establish a caller so we have something to remove.
        string caller3Path = Path.Combine(_fixture.FixtureDir, "MinimalLib", "Caller3.cs");
        await File.WriteAllTextAsync(
            caller3Path,
            """
            namespace MinimalLib;

            public sealed class Caller3
            {
                private readonly A _target = new();

                public void Invoke() { _target.M(); }
            }
            """,
            cts.Token);

        // Poll until Caller3.cs appears, confirming the daemon loaded it.
        string? afterAdd = await PollUntilStdoutContainsAsync(
            "Caller3.cs",
            cts.Token);
        afterAdd.ShouldNotBeNull(
            "Caller3.cs must appear in find-refs before the removal step; " +
            "the test cannot proceed without establishing the caller first.");

        // Act — remove Caller3.cs.
        File.Delete(caller3Path);

        // Poll find-refs until Caller3.cs is no longer in stdout, or the deadline expires.
        // We poll until the result stabilises without the removed reference.
        string? finalStdout = await PollUntilStdoutExcludesAsync(
            "Caller3.cs",
            cts.Token);

        // Assert — the stale reference must be gone after the full reload.
        // This is the precise guard for issue #87: "daemon reported reloading then STILL
        // returned C.cs:1" — the removed reference must not survive the reload.
        finalStdout.ShouldNotBeNull(
            $"find-refs for {TargetSymbolFqn} should no longer return Caller3.cs within {DeadlineSeconds}s " +
            "after deletion — the daemon is serving stale results (issue #87 regression).");
        finalStdout.Contains("Caller3.cs", StringComparison.Ordinal).ShouldBeFalse(
            $"Caller3.cs must not appear after deletion; actual stdout: {finalStdout}");
    }

    /// <summary>
    /// Polls find-refs for <see cref="TargetSymbolFqn"/> every <see cref="PollInterval"/>
    /// until stdout contains <paramref name="expected"/> or the cancellation token fires.
    /// Returns the matching stdout, or null on deadline expiry.
    ///
    /// A Reloading/non-zero exit is treated as "not yet" and does not fail the poll.
    /// </summary>
    private async Task<string?> PollUntilStdoutContainsAsync(
        string expected,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, cancellationToken);

            (string stdout, _, int exitCode) = await _fixture.RunClientWithOutputAsync(
                ["find-refs", TargetSymbolFqn, _fixture.FixtureSlnxPath],
                cancellationToken);

            // Non-zero exit may indicate Reloading or Loading — continue polling.
            if (exitCode != 0)
            {
                continue;
            }

            if (stdout.Contains(expected, StringComparison.Ordinal))
            {
                return stdout;
            }
        }

        return null;
    }

    /// <summary>
    /// Polls find-refs for <see cref="TargetSymbolFqn"/> every <see cref="PollInterval"/>
    /// until stdout does NOT contain <paramref name="excluded"/> or the cancellation token fires.
    /// Returns the first stdout that excludes the string, or null on deadline expiry.
    ///
    /// A Reloading/non-zero exit is treated as "not yet" and does not fail the poll.
    /// </summary>
    private async Task<string?> PollUntilStdoutExcludesAsync(
        string excluded,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, cancellationToken);

            (string stdout, _, int exitCode) = await _fixture.RunClientWithOutputAsync(
                ["find-refs", TargetSymbolFqn, _fixture.FixtureSlnxPath],
                cancellationToken);

            // Non-zero exit may indicate Reloading or Loading — continue polling.
            if (exitCode != 0)
            {
                continue;
            }

            if (!stdout.Contains(excluded, StringComparison.Ordinal))
            {
                return stdout;
            }
        }

        return null;
    }
}
