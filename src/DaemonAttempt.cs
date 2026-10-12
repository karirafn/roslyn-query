namespace RoslynQuery;

/// <summary>Discriminated outcome of a single <see cref="DaemonClient.TryExecuteAsync"/> attempt.</summary>
public enum DaemonOutcome
{
    /// <summary>The daemon returned a result (success or error-as-result). Check <see cref="DaemonAttempt.ExitCode"/>.</summary>
    Result,

    /// <summary>The daemon is still loading the initial workspace.</summary>
    Loading,

    /// <summary>The daemon is reloading after a staleness detection.</summary>
    Reloading,

    /// <summary>The response was not received within the post-handshake deadline.</summary>
    ResponseTimeout,

    /// <summary>
    /// The daemon replied with a malformed or short frame (old protocol or corrupted),
    /// or returned <see cref="ResponseStatus.VersionMismatch"/>.
    /// The caller should kill (after identity verification) and respawn.
    /// </summary>
    MalformedOrShort,

    /// <summary>
    /// The handshake (connect + status-byte read) exceeded the 5-second deadline.
    /// The caller should kill (after identity verification) and cold-start.
    /// </summary>
    Wedged,

    /// <summary>No daemon is running for this solution — connect was refused.</summary>
    NotRunning,
}

/// <summary>Result of a single daemon connection attempt.</summary>
public readonly record struct DaemonAttempt(DaemonOutcome Outcome, int ExitCode);
