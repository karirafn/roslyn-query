using Microsoft.CodeAnalysis;

namespace RoslynQuery;

public sealed class ReloadState
{
    private readonly Lock _lock = new();
    private Solution _solution;
    private IReadOnlyList<string> _trackedPaths;
    private DateTime _lastWriteTime;
    private bool _reloading;

    public ReloadState(Solution solution, IReadOnlyList<string> trackedPaths)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(trackedPaths);

        _solution = solution;
        _trackedPaths = trackedPaths;
        _lastWriteTime = TrackedFiles.ComputeMaxWriteTime(trackedPaths);
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

    public bool IsStale()
    {
        IReadOnlyList<string> paths;
        DateTime lastWriteTime;

        lock (_lock)
        {
            paths = _trackedPaths;
            lastWriteTime = _lastWriteTime;
        }

        return TrackedFiles.ComputeMaxWriteTime(paths) > lastWriteTime;
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

    public void CompleteReload(Solution solution, IReadOnlyList<string> trackedPaths)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(trackedPaths);

        DateTime lastWriteTime = TrackedFiles.ComputeMaxWriteTime(trackedPaths);

        lock (_lock)
        {
            _solution = solution;
            _trackedPaths = trackedPaths;
            _lastWriteTime = lastWriteTime;
            _reloading = false;
        }
    }

    public void AbortReload()
    {
        lock (_lock)
        {
            _reloading = false;
        }
    }
}
