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
    private LoadHealth _health;

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
        if (structuralDirty)
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
        LoadHealth health)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(trackedPaths);
        ArgumentNullException.ThrowIfNull(documentPaths);
        ArgumentNullException.ThrowIfNull(health);

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

    public void CompleteReload(
        Solution solution,
        IReadOnlyList<string> trackedPaths,
        FrozenSet<string> documentPaths,
        LoadHealth health)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(trackedPaths);
        ArgumentNullException.ThrowIfNull(documentPaths);
        ArgumentNullException.ThrowIfNull(health);

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
            _reloading = false;
        }
    }

    /// <summary>
    /// Returns the solution, document paths, and health as a consistent atomic snapshot.
    /// All three fields are captured under a single lock acquisition so a concurrent
    /// <see cref="CompleteReload"/> cannot interleave a pre-reload field with a post-reload one.
    /// </summary>
    public (Solution Solution, FrozenSet<string> DocumentPaths, LoadHealth Health) Snapshot()
    {
        lock (_lock)
        {
            return (_solution, _documentPaths, _health);
        }
    }

    public void AbortReload()
    {
        lock (_lock)
        {
            _reloading = false;
        }
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
