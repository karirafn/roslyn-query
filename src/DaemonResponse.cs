namespace RoslynQuery;

/// <summary>The decoded response from a daemon connection, including status and body fields.</summary>
public readonly record struct DaemonResponse(
    ResponseStatus Status,
    string Stdout,
    string Stderr,
    int ExitCode,
    string ErrorMessage);
