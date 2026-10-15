using System.Collections.Frozen;
using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynQuery;

public sealed class ReloadState
{
    // Backoff schedule for failed reloads: first failure has no delay; subsequent failures
    // back off by doubling up to MaxBackoff. The counter resets on a successful CompleteReload.
    private static readonly TimeSpan[] BackoffSchedule =
    [
        TimeSpan.Zero,          // 1st abort: retry immediately on the next interval tick
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(32),
        TimeSpan.FromSeconds(60), // cap
    ];

    private readonly Lock _lock = new();
    private Solution _solution;
    private IReadOnlyList<string> _trackedPaths;
    private FrozenSet<string> _documentPaths;
    private FrozenDictionary<string, DateTime> _documentWriteTimes;
    private DateTime _lastWriteTime;
    private bool _reloading;

    // When AbortReload is called, this flag is set so the next ClassifyChangeAsync returns Full,
    // prompting the daemon to retry the reload automatically without stranding stale state.
    private bool _dirtyAfterAbort;

    // How many times the current reload attempt has been aborted without a successful CompleteReload.
    // Used to index into BackoffSchedule. Reset to 0 by CompleteReload.
    private int _abortCount;

    // The earliest UTC time at which ClassifyChangeAsync may return Full due to _dirtyAfterAbort.
    // Set to UtcNow + backoff on each abort beyond the first. DateTime.MinValue means no cooldown.
    // A genuine structural-dirty watcher signal bypasses this gate.
    private DateTime _nextRetryAllowedAt;

    private LoadHealth _health;

    // Nullable: null only during the initial empty-solution startup window before CompleteInitialLoad.
    // CompleteInitialLoad and CompleteReload always supply a non-null lease.
    private WorkspaceLease? _lease;

    public ReloadState(
        Solution solution,
        IReadOnlyList<string> trackedPaths,
        FrozenSet<string>? documentPaths = null,
        LoadHealth? health = null)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(trackedPaths);

        _solution = solution;
        _trackedPaths = trackedPaths;
        _documentPaths = documentPaths ?? FrozenSet<string>.Empty;
        _documentWriteTimes = TrackedFiles.ComputeDocumentWriteTimes(_documentPaths);
        _lastWriteTime = TrackedFiles.ComputeMaxWriteTime(trackedPaths);
        _health = health ?? LoadHealth.Complete;
    }

    public Solution Solution
    {
        get
        {
            lock (_lock)
            {
                return _solution;
            }
        }
    }

    public LoadHealth Health
    {
        get
        {
            lock (_lock)
            {
                return _health;
            }
        }
    }

    public IReadOnlyList<string> TrackedPaths
    {
        get
        {
            lock (_lock)
            {
                return _trackedPaths;
            }
        }
    }

    public FrozenSet<string> DocumentPaths
    {
        get
        {
            lock (_lock)
            {
                return _documentPaths;
            }
        }
    }

    public DateTime LastWriteTime
    {
        get
        {
            lock (_lock)
            {
                return _lastWriteTime;
            }
        }
    }

    public async Task<StalenessVerdict> ClassifyChangeAsync(bool structuralDirty)
    {
        // Capture all relevant state under one lock — avoids a second acquisition and
        // ensures the dirty flag and file-state snapshot are always consistent.
        bool aborted;
        DateTime nextRetryAllowedAt;
        IReadOnlyList<string> trackedPaths;
        DateTime lastWriteTime;
        FrozenDictionary<string, DateTime> documentWriteTimes;

        lock (_lock)
        {
            aborted = _dirtyAfterAbort;
            nextRetryAllowedAt = _nextRetryAllowedAt;
            trackedPaths = _trackedPaths;
            lastWriteTime = _lastWriteTime;
            documentWriteTimes = _documentWriteTimes;
        }

        // A genuine watcher signal (new file, deleted file, .csproj change) always forces a Full
        // reload regardless of any ongoing backoff cooldown — do not suppress real file-system events.
        if (structuralDirty)
        {
            return StalenessVerdict.Full;
        }

        // Aborted-reload retry: return Full only after the backoff cooldown has elapsed.
        // This bounds the tight retry loop that would otherwise spin every 2 s on a persistently
        // broken solution — pre-S1 that loop also leaked a workspace on every iteration.
        if (aborted && DateTime.UtcNow >= nextRetryAllowedAt)
        {
            return StalenessVerdict.Full;
        }

        return await Task.Run(() =>
        {
            DateTime maxWriteTime = TrackedFiles.ComputeMaxWriteTime(trackedPaths);

            if (maxWriteTime > lastWriteTime)
            {
                return StalenessVerdict.Full;
            }

            (bool structuralChange, IReadOnlyList<(string Path, DateTime WriteTime)> edited) =
                TrackedFiles.DetectChangedDocuments(documentWriteTimes);

            if (structuralChange)
            {
                return StalenessVerdict.Full;
            }

            if (edited.Count > 0)
            {
                return StalenessVerdict.Incremental;
            }

            return StalenessVerdict.None;
        });
    }

    /// <summary>
    /// Publishes the first real snapshot after the background initial load completes.
    /// Must be called exactly once, before <see cref="TryBeginReload"/> is ever invoked.
    /// </summary>
    public void CompleteInitialLoad(
        Solution solution,
        IReadOnlyList<string> trackedPaths,
        FrozenSet<string> documentPaths,
        LoadHealth health,
        WorkspaceLease lease)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(trackedPaths);
        ArgumentNullException.ThrowIfNull(documentPaths);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(lease);

        DateTime lastWriteTime = TrackedFiles.ComputeMaxWriteTime(trackedPaths);
        FrozenDictionary<string, DateTime> documentWriteTimes =
            TrackedFiles.ComputeDocumentWriteTimes(documentPaths);

        lock (_lock)
        {
            _solution = solution;
            _trackedPaths = trackedPaths;
            _documentPaths = documentPaths;
            _documentWriteTimes = documentWriteTimes;
            _lastWriteTime = lastWriteTime;
            _health = health;
            _lease = lease;
        }
    }

    public bool TryBeginReload()
    {
        lock (_lock)
        {
            if (_reloading)
            {
                return false;
            }

            _reloading = true;
            return true;
        }
    }

    /// <summary>
    /// Commits the results of a completed reload.
    /// Swaps in the new lease and retires the old one — both under the same lock acquisition
    /// that <see cref="Snapshot"/> uses for <see cref="WorkspaceLease.AddRef"/>, so a snapshot
    /// taken before the swap always AddRefs the old lease before it is retired, and a snapshot
    /// taken after the swap sees only the new lease.
    /// </summary>
    public void CompleteReload(
        Solution solution,
        IReadOnlyList<string> trackedPaths,
        FrozenSet<string> documentPaths,
        LoadHealth health,
        WorkspaceLease lease)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(trackedPaths);
        ArgumentNullException.ThrowIfNull(documentPaths);
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(lease);

        DateTime lastWriteTime = TrackedFiles.ComputeMaxWriteTime(trackedPaths);
        FrozenDictionary<string, DateTime> documentWriteTimes =
            TrackedFiles.ComputeDocumentWriteTimes(documentPaths);

        WorkspaceLease? oldLease;

        lock (_lock)
        {
            oldLease = _lease;
            _solution = solution;
            _trackedPaths = trackedPaths;
            _documentPaths = documentPaths;
            _documentWriteTimes = documentWriteTimes;
            _lastWriteTime = lastWriteTime;
            _health = health;
            _lease = lease;
            _reloading = false;
            _dirtyAfterAbort = false;
            _abortCount = 0;
            _nextRetryAllowedAt = DateTime.MinValue;
        }

        // Retire outside the lock: Retire() calls MaybeDispose() which may dispose the workspace,
        // and disposal should not be held under the state lock. The critical invariant — that any
        // concurrent Snapshot() either AddRefs the old lease before it is retired or sees the new
        // lease — is upheld because both AddRef (in Snapshot) and the _lease swap happen under the
        // same lock, so they cannot interleave.
        oldLease?.Retire();
    }

    /// <summary>
    /// Returns the solution, document paths, health, and lease as a consistent atomic snapshot.
    /// Calls <see cref="WorkspaceLease.AddRef"/> on the current lease inside the lock so the ref
    /// is established before any concurrent <see cref="CompleteReload"/> can retire the old lease.
    /// Callers MUST call <see cref="WorkspaceLease.Release"/> on the returned lease when done.
    /// </summary>
    public (Solution Solution, FrozenSet<string> DocumentPaths, LoadHealth Health, WorkspaceLease Lease)
        Snapshot()
    {
        lock (_lock)
        {
            // AddRef inside the lock — see critical-section comment on CompleteReload.
            _lease?.AddRef();
            return (_solution, _documentPaths, _health, _lease!);
        }
    }

    /// <summary>
    /// Resets the reloading flag so another <see cref="TryBeginReload"/> can succeed.
    /// Sets <see cref="_dirtyAfterAbort"/> so the next <see cref="ClassifyChangeAsync"/>
    /// returns <see cref="StalenessVerdict.Full"/>, causing the daemon to retry the reload
    /// automatically rather than stranding stale state indefinitely.
    ///
    /// <para>After this call, <c>ClassifyChangeAsync(structuralDirty: false)</c> returns
    /// <see cref="StalenessVerdict.Full"/> once the per-abort backoff cooldown has elapsed
    /// (see <see cref="BackoffSchedule"/>). A genuine watcher-supplied <c>structuralDirty = true</c>
    /// bypasses the cooldown entirely. <c>_dirtyAfterAbort</c> is cleared by a successful
    /// <see cref="CompleteReload"/>, which also resets the abort counter.</para>
    /// </summary>
    public void AbortReload()
    {
        lock (_lock)
        {
            _reloading = false;
            _dirtyAfterAbort = true;

            // Schedule the next retry after the backoff for this abort number.
            // BackoffSchedule[0] = TimeSpan.Zero so the first abort retries immediately;
            // each subsequent abort doubles the cooldown up to the cap.
            // _abortCount is incremented after indexing so the next AbortReload advances the schedule.
            int index = Math.Min(_abortCount, BackoffSchedule.Length - 1);
            _nextRetryAllowedAt = DateTime.UtcNow + BackoffSchedule[index];
            _abortCount = Math.Min(_abortCount + 1, BackoffSchedule.Length - 1);
        }
    }

    /// <summary>
    /// Detects any documents whose on-disk mtime has advanced since the baseline, reads their
    /// new content, and applies in-process edits — all without the caller having to supply the
    /// edited-paths list. This avoids the TOCTOU gap that would exist if
    /// <see cref="ClassifyChangeAsync"/> returned the list and the daemon passed it back in a
    /// separate call.
    /// </summary>
    public Task ApplyPendingIncrementalEditsAsync()
    {
        FrozenDictionary<string, DateTime> documentWriteTimes;
        lock (_lock)
        {
            documentWriteTimes = _documentWriteTimes;
        }

        return Task.Run(() =>
        {
            (_, IReadOnlyList<(string Path, DateTime WriteTime)> edited) =
                TrackedFiles.DetectChangedDocuments(documentWriteTimes);

            if (edited.Count > 0)
            {
                TryApplyIncrementalEdits(edited);
            }
        });
    }

    internal void TryApplyIncrementalEdits(IReadOnlyList<(string Path, DateTime WriteTime)> edited)
    {
        ArgumentNullException.ThrowIfNull(edited);

        Solution currentSolution;
        FrozenDictionary<string, DateTime> currentWriteTimes;

        lock (_lock)
        {
            currentSolution = _solution;
            currentWriteTimes = _documentWriteTimes;
        }

        // Capturing solution and write-times before the lock, mutating them, and writing back
        // under the lock is safe against a concurrent CompleteReload for two reasons:
        // 1. TryBeginReload gates a concurrent Full reload — a reload cannot proceed while
        //    incremental edits are being prepared.
        // 2. If a structural change races in and is detected by the next ClassifyChangeAsync,
        //    that call returns Full and triggers a fresh MSBuild reload, so any stale write-back
        //    from this incremental pass is bounded to at most one staleness-check interval.

        Solution updatedSolution = currentSolution;
        Dictionary<string, DateTime> updatedWriteTimes = new(
            currentWriteTimes,
            StringComparer.OrdinalIgnoreCase);

        bool anyApplied = false;

        foreach ((string path, DateTime writeTime) in edited)
        {
            ImmutableArray<DocumentId> documentIds = updatedSolution.GetDocumentIdsWithFilePath(path);

            if (documentIds.IsEmpty)
            {
                continue;
            }

            string content;

            try
            {
                content = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or FileNotFoundException)
            {
                continue;
            }

            SourceText sourceText = SourceText.From(content);

            foreach (DocumentId id in documentIds)
            {
                updatedSolution = updatedSolution.WithDocumentText(id, sourceText);
            }

            updatedWriteTimes[path] = writeTime;
            anyApplied = true;
        }

        if (!anyApplied)
        {
            return;
        }

        FrozenDictionary<string, DateTime> frozenUpdatedWriteTimes =
            updatedWriteTimes.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            _solution = updatedSolution;
            _documentWriteTimes = frozenUpdatedWriteTimes;
        }
    }

    /// <summary>
    /// Test seam: overrides <see cref="_nextRetryAllowedAt"/> so backoff cooldown tests can
    /// simulate elapsed time without real sleeps. Production code never calls this.
    /// </summary>
    internal void SetNextRetryAllowedAtForTests(DateTime nextRetryAllowedAt)
    {
        lock (_lock)
        {
            _nextRetryAllowedAt = nextRetryAllowedAt;
        }
    }
}
