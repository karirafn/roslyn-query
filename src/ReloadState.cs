using System.Collections.Frozen;
using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynQuery;

public sealed class ReloadState
{
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
        bool aborted;
        lock (_lock)
        {
            aborted = _dirtyAfterAbort;
        }

        if (structuralDirty || aborted)
        {
            return StalenessVerdict.Full;
        }

        IReadOnlyList<string> trackedPaths;
        DateTime lastWriteTime;
        FrozenDictionary<string, DateTime> documentWriteTimes;

        lock (_lock)
        {
            trackedPaths = _trackedPaths;
            lastWriteTime = _lastWriteTime;
            documentWriteTimes = _documentWriteTimes;
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
    /// Also sets <see cref="_dirtyAfterAbort"/> so the next <see cref="ClassifyChangeAsync"/>
    /// returns <see cref="StalenessVerdict.Full"/>, causing the daemon to retry the reload
    /// automatically rather than stranding stale state indefinitely.
    ///
    /// <para>Contract for step 8 (daemon wiring): after <c>AbortReload</c> returns, the next
    /// call to <c>ClassifyChangeAsync(structuralDirty: false)</c> will return <see cref="StalenessVerdict.Full"/>
    /// regardless of file timestamps. The daemon's staleness-check loop therefore triggers a
    /// fresh <see cref="TryBeginReload"/> on the next interval tick without any additional
    /// watcher signal. <c>_dirtyAfterAbort</c> is cleared by a successful
    /// <see cref="CompleteReload"/>.</para>
    /// </summary>
    public void AbortReload()
    {
        lock (_lock)
        {
            _reloading = false;
            _dirtyAfterAbort = true;
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

    public void TryApplyIncrementalEdits(IReadOnlyList<(string Path, DateTime WriteTime)> edited)
    {
        ArgumentNullException.ThrowIfNull(edited);

        Solution currentSolution;
        FrozenDictionary<string, DateTime> currentWriteTimes;

        lock (_lock)
        {
            currentSolution = _solution;
            currentWriteTimes = _documentWriteTimes;
        }

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
}
