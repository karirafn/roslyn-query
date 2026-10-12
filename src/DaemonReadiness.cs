namespace RoslynQuery;

/// <summary>The readiness states of the daemon workspace.</summary>
public enum DaemonReadinessState
{
    /// <summary>The initial workspace load is in progress.</summary>
    Loading,

    /// <summary>The workspace is loaded and ready to serve queries.</summary>
    Ready,

    /// <summary>A staleness-triggered reload is in progress.</summary>
    Reloading,
}

/// <summary>
/// Thread-safe readiness state machine for the daemon workspace lifecycle.
/// Starts in <see cref="DaemonReadinessState.Loading"/>.
/// Valid transitions: Loading → Ready, Ready → Reloading, Reloading → Ready.
/// </summary>
internal sealed class DaemonReadiness
{
    private readonly Lock _lock = new();
    private DaemonReadinessState _state = DaemonReadinessState.Loading;

    /// <summary>Gets the current readiness state. Thread-safe.</summary>
    public DaemonReadinessState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>Transitions from <see cref="DaemonReadinessState.Loading"/> or
    /// <see cref="DaemonReadinessState.Reloading"/> to <see cref="DaemonReadinessState.Ready"/>.</summary>
    public void TransitionToReady()
    {
        lock (_lock)
        {
            _state = DaemonReadinessState.Ready;
        }
    }

    /// <summary>Transitions from <see cref="DaemonReadinessState.Ready"/> to
    /// <see cref="DaemonReadinessState.Reloading"/>.</summary>
    public void TransitionToReloading()
    {
        lock (_lock)
        {
            _state = DaemonReadinessState.Reloading;
        }
    }
}
