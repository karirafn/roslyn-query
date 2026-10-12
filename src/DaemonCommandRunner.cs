namespace RoslynQuery;

/// <summary>
/// Orchestrates a single command or batch execution against the daemon,
/// with injectable seams for testing without real processes.
/// </summary>
public static class DaemonCommandRunner
{
    private static readonly TimeSpan DefaultLoadingDeadline = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DefaultProgressCadence = TimeSpan.FromSeconds(5);

    // AsyncLocal seams mirroring PipeProtocol.SetStateDirectoryOverrideForTests
    private static readonly AsyncLocal<TimeSpan?> s_loadingDeadlineOverride = new();
    private static readonly AsyncLocal<TimeSpan?> s_progressCadenceOverride = new();

    private static TimeSpan LoadingDeadline =>
        s_loadingDeadlineOverride.Value ?? DefaultLoadingDeadline;

    private static TimeSpan ProgressCadence =>
        s_progressCadenceOverride.Value ?? DefaultProgressCadence;

    /// <summary>Overrides the loading deadline for the current async context (tests only).</summary>
    internal static void SetLoadingDeadlineForTests(TimeSpan? deadline) =>
        s_loadingDeadlineOverride.Value = deadline;

    /// <summary>Overrides the loading progress cadence for the current async context (tests only).</summary>
    internal static void SetLoadingProgressCadenceForTests(TimeSpan? cadence) =>
        s_progressCadenceOverride.Value = cadence;

    /// <summary>
    /// Delegate type for attempting a daemon connection.
    /// </summary>
    public delegate Task<DaemonAttempt> TryExecuteHandler(
        string solutionPath,
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken);

    /// <summary>
    /// Delegate type for running a command directly (in-process fallback).
    /// </summary>
    public delegate Task<int> RunDirectHandler(string solutionPath, string[] args);

    /// <summary>
    /// Runs a command against the daemon with orchestration: spawn/kill/respawn/RunDirect fallback.
    /// </summary>
    /// <param name="solutionPath">Full path to the solution file.</param>
    /// <param name="args">Command arguments.</param>
    /// <param name="stdout">Standard output writer.</param>
    /// <param name="stderr">Standard error writer.</param>
    /// <param name="tryExecute">Injectable delegate for daemon connection attempt.</param>
    /// <param name="spawn">Injectable action to spawn a daemon.</param>
    /// <param name="kill">Injectable action to kill a running daemon.</param>
    /// <param name="runDirect">Injectable function for in-process execution (RunDirect fallback).</param>
    /// <param name="clock">Injectable clock returning elapsed loading time.</param>
    /// <param name="isDaemonAliveAfterSpawn">Injectable predicate to check if daemon is alive after spawn.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<int> RunAsync(
        string solutionPath,
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        TryExecuteHandler tryExecute,
        Action<string> spawn,
        Action<string> kill,
        RunDirectHandler runDirect,
        Func<TimeSpan> clock,
        Func<string, bool>? isDaemonAliveAfterSpawn = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tryExecute);
        ArgumentNullException.ThrowIfNull(spawn);
        ArgumentNullException.ThrowIfNull(kill);
        ArgumentNullException.ThrowIfNull(runDirect);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        isDaemonAliveAfterSpawn ??= DefaultIsDaemonAliveAfterSpawn;

        bool spawned = false;
        TimeSpan? lastProgressAt = null;

        while (true)
        {
            DaemonAttempt attempt = await tryExecute(
                    solutionPath,
                    args,
                    stdout,
                    stderr,
                    cancellationToken)
                .ConfigureAwait(false);

            switch (attempt.Outcome)
            {
                case DaemonOutcome.Result:
                    return attempt.ExitCode;

                case DaemonOutcome.ResponseTimeout:
                    await stderr.WriteLineAsync("daemon did not respond; retry").ConfigureAwait(false);
                    return ExitCodes.Transient;

                case DaemonOutcome.Loading or DaemonOutcome.Reloading:
                {
                    TimeSpan elapsed = clock();
                    if (elapsed >= LoadingDeadline)
                    {
                        await stderr.WriteLineAsync(
                                "daemon still loading; retry - load continues in background")
                            .ConfigureAwait(false);
                        return ExitCodes.Transient;
                    }

                    if (lastProgressAt is null || elapsed - lastProgressAt.Value >= ProgressCadence)
                    {
                        lastProgressAt = elapsed;
                        await stderr.WriteLineAsync(
                                $"daemon: loading workspace ({(int)elapsed.TotalSeconds}s)...")
                            .ConfigureAwait(false);
                    }

                    // Brief wait before retrying
                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                case DaemonOutcome.MalformedOrShort or DaemonOutcome.Wedged:
                    kill(solutionPath);
                    spawn(solutionPath);
                    spawned = true;

                    if (!isDaemonAliveAfterSpawn(solutionPath))
                    {
                        return await runDirect(solutionPath, args).ConfigureAwait(false);
                    }

                    continue;

                case DaemonOutcome.NotRunning:
                    if (spawned)
                    {
                        // Already spawned — daemon still not running means it died immediately
                        return await runDirect(solutionPath, args).ConfigureAwait(false);
                    }

                    spawn(solutionPath);
                    spawned = true;

                    if (!isDaemonAliveAfterSpawn(solutionPath))
                    {
                        return await runDirect(solutionPath, args).ConfigureAwait(false);
                    }

                    continue;

                default:
                    // Should never happen — DaemonOutcome is exhaustive
                    return await runDirect(solutionPath, args).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Runs a batch of command lines against the daemon.
    /// Applies the loading wait once before the first line using the provided attempt delegate;
    /// per-line errors continue the batch.
    /// </summary>
    public static async Task<int> RunBatchAsync(
        string solutionPath,
        IEnumerable<string> lines,
        string[] globalFlags,
        TextWriter stdout,
        TextWriter stderr,
        TryExecuteHandler tryExecute,
        Action<string> spawn,
        Action<string> kill,
        RunDirectHandler runDirect,
        Func<TimeSpan> clock,
        Func<string, bool>? isDaemonAliveAfterSpawn = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(tryExecute);
        ArgumentNullException.ThrowIfNull(spawn);
        ArgumentNullException.ThrowIfNull(kill);
        ArgumentNullException.ThrowIfNull(runDirect);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        isDaemonAliveAfterSpawn ??= DefaultIsDaemonAliveAfterSpawn;

        // Wait for daemon readiness before the first line
        bool ready = await WaitForReadyAsync(
                solutionPath,
                stderr,
                tryExecute,
                spawn,
                kill,
                isDaemonAliveAfterSpawn,
                clock,
                cancellationToken)
            .ConfigureAwait(false);

        if (!ready)
        {
            await stderr.WriteLineAsync("daemon still loading; no commands run").ConfigureAwait(false);
            return ExitCodes.Transient;
        }

        int lastExitCode = 0;

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            await stdout.WriteLineAsync($"=== {line} ===").ConfigureAwait(false);

            string[] subArgs = LineTokenizer.Tokenize(line);
            string[] fullArgs = [.. globalFlags, .. subArgs];

            DaemonAttempt attempt = await tryExecute(
                    solutionPath,
                    fullArgs,
                    stdout,
                    stderr,
                    cancellationToken)
                .ConfigureAwait(false);

            switch (attempt.Outcome)
            {
                case DaemonOutcome.Result:
                    lastExitCode = attempt.ExitCode;
                    break;

                case DaemonOutcome.ResponseTimeout:
                    await stderr.WriteLineAsync($"error: daemon did not respond for command: {line}")
                        .ConfigureAwait(false);
                    lastExitCode = ExitCodes.Transient;
                    break;

                default:
                    await stderr.WriteLineAsync($"error: daemon unavailable for command: {line}")
                        .ConfigureAwait(false);
                    lastExitCode = 1;
                    break;
            }
        }

        return lastExitCode;
    }

    // Waits for the daemon to become ready (not Loading/Reloading).
    // Returns true when ready, false when the loading deadline expired or daemon died.
    private static async Task<bool> WaitForReadyAsync(
        string solutionPath,
        TextWriter stderr,
        TryExecuteHandler tryExecute,
        Action<string> spawn,
        Action<string> kill,
        Func<string, bool> isDaemonAliveAfterSpawn,
        Func<TimeSpan> clock,
        CancellationToken cancellationToken)
    {
        bool spawned = false;
        TimeSpan? lastProgressAt = null;

        while (true)
        {
            // Probe readiness using a sentinel args array; the server will respond with its
            // current status (Loading, Reloading, or Result if it received an unknown command).
            // Since the server returns Result for unknown commands (dispatches and exits 1),
            // any Result outcome means the daemon is ready to serve.
            DaemonAttempt attempt = await tryExecute(
                    solutionPath,
                    ["--daemon-status-probe"],
                    TextWriter.Null,
                    TextWriter.Null,
                    cancellationToken)
                .ConfigureAwait(false);

            switch (attempt.Outcome)
            {
                case DaemonOutcome.Result:
                    return true;

                case DaemonOutcome.Loading or DaemonOutcome.Reloading:
                {
                    TimeSpan elapsed = clock();
                    if (elapsed >= LoadingDeadline)
                    {
                        return false;
                    }

                    if (lastProgressAt is null || elapsed - lastProgressAt.Value >= ProgressCadence)
                    {
                        lastProgressAt = elapsed;
                        await stderr.WriteLineAsync(
                                $"daemon: loading workspace ({(int)elapsed.TotalSeconds}s)...")
                            .ConfigureAwait(false);
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                case DaemonOutcome.MalformedOrShort or DaemonOutcome.Wedged:
                    kill(solutionPath);
                    spawn(solutionPath);
                    spawned = true;

                    if (!isDaemonAliveAfterSpawn(solutionPath))
                    {
                        return false;
                    }

                    continue;

                case DaemonOutcome.NotRunning:
                    if (spawned)
                    {
                        return false;
                    }

                    spawn(solutionPath);
                    spawned = true;

                    if (!isDaemonAliveAfterSpawn(solutionPath))
                    {
                        return false;
                    }

                    continue;

                default:
                    return false;
            }
        }
    }

    private static bool DefaultIsDaemonAliveAfterSpawn(string solutionPath)
    {
        (int Pid, long? StartTimeTicks)? record = DaemonProcess.ReadPidRecord(solutionPath);
        if (record is null)
        {
            return false;
        }

        return DaemonProcess.IsProcessRunning(record.Value.Pid);
    }
}
