namespace RoslynQuery;

/// <summary>
/// Well-known exit codes for the roslyn-query CLI.
/// </summary>
public static class ExitCodes
{
    /// <summary>
    /// 75 = EX_TEMPFAIL (BSD sysexits.h) — temporary failure; the caller should retry.
    /// Used by the daemon to signal that the workspace is currently reloading and the
    /// command cannot be served yet. This is the public "retry" contract between the
    /// daemon server and the client.
    /// </summary>
    public const int Transient = 75;
}
