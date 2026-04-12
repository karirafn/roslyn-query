using Microsoft.CodeAnalysis;

namespace RoslynQuery;

public sealed class ReloadState
{
    private readonly Lock _lock = new();
    private Solution _solution;
    private DateTime _lastWriteTime;
    private bool _reloading;

    public ReloadState(Solution solution, DateTime lastWriteTime)
    {
        _solution = solution;
        _lastWriteTime = lastWriteTime;
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

    public void CompleteReload(Solution solution, DateTime lastWriteTime)
    {
        lock (_lock)
        {
            _solution = solution;
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
