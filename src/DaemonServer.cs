using System.Collections.Frozen;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynQuery;

public static class DaemonServer
{
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StalenessCheckInterval = TimeSpan.FromSeconds(2);
    private const int QueryTimeoutSeconds = 60;

    // Allow multiple concurrent connections (concurrent-accept loop).
    // NamedPipeServerStream.MaxAllowedServerInstances = -1 means system-defined max.
    private const int MaxAllowedServerInstances = -1;

    // AsyncLocal seams — mirror DaemonCommandRunner.SetLoadingDeadlineForTests.
    // Value flows with each test's async context; production code never sets them.
    private static readonly AsyncLocal<TimeSpan?> s_idleTimeoutOverride = new();
    private static readonly AsyncLocal<bool> s_suppressEnvironmentExit = new();

    /// <summary>Overrides the idle timeout for the current async context (tests only).</summary>
    internal static void SetIdleTimeoutForTests(TimeSpan? timeout) =>
        s_idleTimeoutOverride.Value = timeout;

    /// <summary>
    /// When true, background-load failures do not call <see cref="Environment.Exit"/> — tests only.
    /// Production code never sets this.
    /// </summary>
    internal static void SetSuppressEnvironmentExitForTests(bool suppress) =>
        s_suppressEnvironmentExit.Value = suppress;

    private static TimeSpan IdleTimeout =>
        s_idleTimeoutOverride.Value ?? DefaultIdleTimeout;

    // Test-only load-delay seam: the daemon sleeps this long inside the background load task
    // before transitioning to Ready, making the Loading window observable in integration tests.
    // Gated behind ROSLYN_QUERY_TEST_ENABLE_SEAMS=1 so a stray env var in production is inert.
    private static TimeSpan? ResolveTestLoadDelay()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("ROSLYN_QUERY_TEST_ENABLE_SEAMS"),
                "1",
                StringComparison.Ordinal))
        {
            return null;
        }

        string? raw = Environment.GetEnvironmentVariable("ROSLYN_QUERY_TEST_LOAD_DELAY_MS");
        if (int.TryParse(raw, out int ms) && ms > 0)
        {
            return TimeSpan.FromMilliseconds(ms);
        }

        return null;
    }

    public static async Task RunAsync(
        string solutionPath,
        CancellationToken cancellationToken = default)
    {
        solutionPath = Path.GetFullPath(solutionPath);
        string pipeName = PipeProtocol.DerivePipeName(solutionPath);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            DaemonProcess.CleanupPidFile(solutionPath);

        string solutionDirectory = Path.GetDirectoryName(solutionPath) ?? "";

        using MSBuildWorkspace workspace = MSBuildWorkspace.Create();
        DaemonReadiness readiness = new();

        // Initialise ReloadState with an empty solution so the accept loop can start
        // immediately. The background load task replaces this with the real snapshot
        // and transitions readiness to Ready once warm-up completes.
        ReloadState reloadState = new(workspace.CurrentSolution, [], FrozenSet<string>.Empty);

        // Shared staleness-check ticks (UTC); DateTime.MinValue.Ticks as sentinel.
        // One connection runs the check per interval; others skip until the next interval elapses.
        // TryBeginReload prevents double-reload even if two connections both pass the interval check.
        long lastStalenessCheckTicks = DateTime.MinValue.Ticks;

        // In-flight counter for idle-timeout awareness.
        int inFlight = 0;

        CancellationTokenSource idleCts = new(IdleTimeout);
        CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idleCts.Token);

        // Resolve any test-injected load delay before starting the background task.
        TimeSpan? testLoadDelay = ResolveTestLoadDelay();

        // Start load + warm-up in the background so the pipe listener can accept
        // connections (and reply Loading) before the workspace is ready.
        _ = Task.Run(
            async () =>
            {
                try
                {
                    // Test-only: pause inside the load task to keep the daemon in Loading state
                    // long enough for integration tests to observe Loading replies and wait behavior.
                    if (testLoadDelay is { } delay)
                    {
                        await Task.Delay(delay, cancellationToken);
                    }

                    LoadHealth health = await SolutionLoader.LoadWithHealthAsync(
                        workspace,
                        solutionPath,
                        cancellationToken);
                    await SolutionLoader.WarmUpAsync(workspace.CurrentSolution, cancellationToken);

                    IReadOnlyList<string> initialTrackedPaths = TrackedFiles.CollectPaths(
                        workspace.CurrentSolution,
                        solutionDirectory,
                        solutionPath);
                    FrozenSet<string> initialDocumentPaths =
                        TrackedFiles.CollectDocumentPaths(workspace.CurrentSolution);

                    reloadState.CompleteInitialLoad(
                        workspace.CurrentSolution,
                        initialTrackedPaths,
                        initialDocumentPaths,
                        health);

                    readiness.TransitionToReady();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Caller requested shutdown — exit cleanly.
                }
#pragma warning disable CA1031 // Load failure must fail closed: process exits so the client falls back to RunDirect
                catch
#pragma warning restore CA1031
                {
                    // A load failure is unrecoverable — exit so the client's spawn-but-dead
                    // detection (ReadPidFile + IsProcessRunning) routes it through RunDirect.
                    // In-process tests suppress Environment.Exit via SetSuppressEnvironmentExitForTests.
                    if (!s_suppressEnvironmentExit.Value)
                    {
                        Environment.Exit(1);
                    }
                }
            },
            cancellationToken);

        try
        {
            while (!linkedCts.Token.IsCancellationRequested)
            {
                NamedPipeServerStream? pipe = null;
                try
                {
#pragma warning disable CA2000 // Disposed inside the per-connection task
                    pipe = CreatePipeServer(pipeName);
#pragma warning restore CA2000

                    await pipe.WaitForConnectionAsync(linkedCts.Token);

                    // Reset idle timer when no other connection is in flight.
                    if (Volatile.Read(ref inFlight) == 0)
                    {
                        ResetIdleTimer(ref idleCts, ref linkedCts, cancellationToken);
                    }

                    // Hand the pipe off to a connection task; clear the local so the outer
                    // catch does not double-dispose it.
                    NamedPipeServerStream connectionPipe = pipe;
                    pipe = null;

                    Interlocked.Increment(ref inFlight);

                    // Capture mutable state for the connection task via closures — the task
                    // owns connectionPipe's lifetime and disposes it in its finally block.
                    // CancellationToken.None is intentional: passing linkedCts.Token would allow
                    // the scheduler to skip the task when the token is already cancelled, which
                    // would bypass the finally block and leak the inFlight counter. The handler
                    // still observes cancellation via cancellationToken and linkedCts.
#pragma warning disable CA2025 // connectionPipe lifetime is owned by the task (disposed in finally)
                    _ = Task.Run(
                        () => RunConnectionAsync(
                            connectionPipe,
                            solutionPath,
                            solutionDirectory,
                            reloadState,
                            readiness,
                            () => Interlocked.Read(ref lastStalenessCheckTicks),
                            ticks => Interlocked.Exchange(ref lastStalenessCheckTicks, ticks),
                            () => Interlocked.Decrement(ref inFlight),
                            linkedCts,
                            cancellationToken),
                        CancellationToken.None);
#pragma warning restore CA2025
                }
                catch (OperationCanceledException)
                {
                    if (pipe is not null)
                    {
                        await pipe.DisposeAsync();
                    }

                    // External shutdown (Ctrl+C, RunDaemon CTS) — always stop immediately.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    // Idle timer fired (idleCts expired, cancellationToken NOT requested).
                    // Only exit when there are truly no requests in flight; otherwise re-arm
                    // the idle timer and keep serving so an in-progress request can complete.
                    if (Volatile.Read(ref inFlight) > 0)
                    {
                        // Requests still in flight — re-arm and continue accepting.
                        // The old linkedCts is already cancelled (idleCts fired); recreate both.
                        ResetIdleTimer(ref idleCts, ref linkedCts, cancellationToken);
                        continue;
                    }

                    // Genuinely idle — exit the accept loop.
                    break;
                }
#pragma warning disable CA1031 // Accept-loop errors (UnauthorizedAccessException, IOException) must not kill the daemon
                catch (Exception) when (!linkedCts.Token.IsCancellationRequested)
#pragma warning restore CA1031
                {
                    // Protocol errors, UnauthorizedAccessException from WaitForConnectionAsync
                    // on a peer with a different uid, and broken-pipe IOExceptions must not
                    // crash the daemon. Dispose the pipe and continue accepting.
                    if (pipe is not null)
                    {
                        await pipe.DisposeAsync();
                    }
                }
            }
        }
        finally
        {
            linkedCts.Dispose();
            idleCts.Dispose();
            DaemonProcess.CleanupPidFile(solutionPath);
        }
    }

    private static async Task RunConnectionAsync(
        NamedPipeServerStream pipe,
        string solutionPath,
        string solutionDirectory,
        ReloadState reloadState,
        DaemonReadiness readiness,
        Func<long> getStalenessCheckTicks,
        Action<long> setStalenessCheckTicks,
        Action onComplete,
        CancellationTokenSource linkedCts,
        CancellationToken cancellationToken)
    {
        try
        {
            await HandleConnectionAsync(
                pipe,
                solutionPath,
                solutionDirectory,
                reloadState,
                readiness,
                getStalenessCheckTicks,
                setStalenessCheckTicks,
                linkedCts,
                cancellationToken);
        }
        finally
        {
            onComplete();
            await pipe.DisposeAsync();
        }
    }

    private static async Task HandleConnectionAsync(
        NamedPipeServerStream pipe,
        string solutionPath,
        string solutionDirectory,
        ReloadState reloadState,
        DaemonReadiness readiness,
        Func<long> getStalenessCheckTicks,
        Action<long> setStalenessCheckTicks,
        CancellationTokenSource linkedCts,
        CancellationToken cancellationToken)
    {
        try
        {
            (int protocolVersion, _, string[] args) =
                await PipeProtocol.ReadRequestEnvelopeAsync(pipe, linkedCts.Token);

            if (protocolVersion != PipeProtocol.ProtocolVersion)
            {
                await PipeProtocol.WriteStatusAsync(
                    pipe,
                    ResponseStatus.VersionMismatch,
                    linkedCts.Token);
                return;
            }

            if (args is [PipeProtocol.ShutdownCommand])
            {
                await PipeProtocol.WriteResultAsync(pipe, "", "", 0, linkedCts.Token);
                pipe.Disconnect();
                await linkedCts.CancelAsync();
                return;
            }

            // Reply with a Loading status while the initial load is still in progress.
            if (readiness.State == DaemonReadinessState.Loading)
            {
                await PipeProtocol.WriteStatusAsync(
                    pipe,
                    ResponseStatus.Loading,
                    linkedCts.Token);
                pipe.Disconnect();
                return;
            }

            bool stale = false;
            DateTime utcNow = DateTime.UtcNow;
            long previousTicks = getStalenessCheckTicks();
            TimeSpan sinceLastCheck = utcNow - new DateTime(previousTicks, DateTimeKind.Utc);

            if (sinceLastCheck >= StalenessCheckInterval)
            {
                // Stamp the tick unconditionally so subsequent connections skip until the
                // next interval. TryBeginReload is the real mutual-exclusion gate for reload.
                setStalenessCheckTicks(utcNow.Ticks);
                stale = await reloadState.IsStaleAsync();
            }

            if (stale)
            {
                await PipeProtocol.WriteStatusAsync(
                    pipe,
                    ResponseStatus.Reloading,
                    linkedCts.Token);
                pipe.Disconnect();

                if (reloadState.TryBeginReload())
                {
                    readiness.TransitionToReloading();

                    _ = Task.Run(
                        async () =>
                        {
                            try
                            {
                                using MSBuildWorkspace reloadWorkspace = MSBuildWorkspace.Create();
                                LoadHealth reloadHealth = await SolutionLoader.LoadWithHealthAsync(
                                    reloadWorkspace,
                                    solutionPath,
                                    cancellationToken);
                                await SolutionLoader.WarmUpAsync(
                                    reloadWorkspace.CurrentSolution,
                                    cancellationToken);
                                IReadOnlyList<string> reloadedPaths = TrackedFiles.CollectPaths(
                                    reloadWorkspace.CurrentSolution,
                                    solutionDirectory,
                                    solutionPath);
                                FrozenSet<string> reloadedDocumentPaths =
                                    TrackedFiles.CollectDocumentPaths(reloadWorkspace.CurrentSolution);
                                reloadState.CompleteReload(
                                    reloadWorkspace.CurrentSolution,
                                    reloadedPaths,
                                    reloadedDocumentPaths,
                                    reloadHealth);
                                readiness.TransitionToReady();
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                // Caller requested shutdown — exit cleanly without aborting state.
                            }
#pragma warning disable CA1031 // Abort reload on any failure to avoid stuck state
                            catch
#pragma warning restore CA1031
                            {
                                reloadState.AbortReload();
                                readiness.TransitionToReady();
                            }
                        },
                        cancellationToken);
                }

                return;
            }

            StringWriter stdoutWriter = new();
            StringWriter stderrWriter = new();
            // The Loading guard above guarantees reloadState.Solution is the real loaded snapshot,
            // not the empty startup placeholder — do not remove that guard without updating this read.
            CommandContext context = new(
                stdoutWriter,
                stderrWriter,
                reloadState.Solution,
                solutionDirectory,
                reloadState.DocumentPaths,
                reloadState.Health);

            bool queryTimedOut = false;
            int exitCode = 0;
#pragma warning disable CA2000 // Both CTS are disposed in the finally block below
            CancellationTokenSource queryCts =
                new(TimeSpan.FromSeconds(QueryTimeoutSeconds));
            CancellationTokenSource queryLinkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    linkedCts.Token,
                    queryCts.Token);
#pragma warning restore CA2000
            try
            {
                exitCode = await CommandDispatcher.ExecuteAsync(
                    args,
                    context,
                    queryLinkedCts.Token);
            }
            catch (OperationCanceledException) when (queryCts.IsCancellationRequested
                && !linkedCts.IsCancellationRequested)
            {
                queryTimedOut = true;
            }
#pragma warning disable CA1031 // Command exceptions must produce an Error reply, not kill the daemon
            catch (Exception ex) when (!linkedCts.Token.IsCancellationRequested)
#pragma warning restore CA1031
            {
                // A command throwing an unexpected exception sends an Error reply so the
                // client sees DaemonOutcome.Result(exit=1) rather than a dropped connection.
                await PipeProtocol.WriteErrorAsync(
                    pipe,
                    ex.Message,
                    linkedCts.Token);
                return;
            }
            finally
            {
                queryLinkedCts.Dispose();
                queryCts.Dispose();
            }

            if (queryTimedOut)
            {
                await PipeProtocol.WriteResultAsync(
                    pipe,
                    "",
                    $"query timed out after {QueryTimeoutSeconds} seconds",
                    1,
                    linkedCts.Token);
                pipe.Disconnect();
                return;
            }

            await PipeProtocol.WriteResultAsync(
                pipe,
                stdoutWriter.ToString(),
                stderrWriter.ToString(),
                exitCode,
                linkedCts.Token);

            pipe.Disconnect();
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested — exit the connection handler cleanly.
        }
#pragma warning disable CA1031 // Per-connection exceptions must not kill the daemon
        catch (Exception) when (!linkedCts.Token.IsCancellationRequested)
#pragma warning restore CA1031
        {
            // Protocol errors (InvalidDataException from frame guard, IOException from broken pipe)
            // and similar per-connection failures must not crash the daemon.
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
    private static bool IsWindows() => OperatingSystem.IsWindows();

    private static NamedPipeServerStream CreatePipeServer(string pipeName)
    {
        if (IsWindows())
        {
            // Owner is set to .Owner (the default-owner SID) so the client's CurrentUserOnly
            // check — which reads the pipe's owner and compares it to its own .Owner — sees a
            // deterministic, explicitly-set value regardless of Windows token-defaulting.
            // The DACL access rule is granted to .User (the account SID) to restrict which
            // processes can connect: on elevated tokens .Owner == BUILTIN\Administrators, so
            // granting the DACL to .Owner would allow any admin-group process to connect.
            // Owner (metadata read by the client check) and DACL (the connect gate) are
            // independent; they intentionally use different SIDs on elevated tokens.
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            // .Owner is non-null for all standard user and service accounts; it is only null
            // in kernel-impersonation scenarios that cannot reach a user-mode named pipe server.
            SecurityIdentifier ownerSid = identity.Owner!;
            PipeSecurity security = new();
            security.SetOwner(ownerSid);
            security.AddAccessRule(new PipeAccessRule(
                // .User is non-null for the same standard account set as .Owner above; it is
                // null only for anonymous/impersonation tokens that cannot bind a pipe server.
                identity.User!,
                PipeAccessRights.FullControl,
                AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(
                pipeName,
                PipeDirection.InOut,
                MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: 0,
                outBufferSize: 0,
                security);
        }

        // On Unix the pipe is a domain socket. CurrentUserOnly makes the runtime compare the
        // connecting peer's effective uid with ours on accept and throw
        // UnauthorizedAccessException on mismatch, which the accept loop's general catch
        // absorbs. Until .NET 11 the socket file's mode follows the process's default
        // file-creation mask, so other users can see it but are rejected before any request
        // is read; .NET 11 chmods it to 0600 at bind:
        // https://learn.microsoft.com/dotnet/core/compatibility/core-libraries/11/namedpipeserverstream-unix-permissions
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
    }

    private static void ResetIdleTimer(
        ref CancellationTokenSource idleCts,
        ref CancellationTokenSource linkedCts,
        CancellationToken externalToken)
    {
        if (idleCts.TryReset())
        {
            idleCts.CancelAfter(IdleTimeout);
            return;
        }

        // Idle CTS already fired — recreate both
        idleCts.Dispose();
        linkedCts.Dispose();
        idleCts = new CancellationTokenSource(IdleTimeout);
        linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            externalToken,
            idleCts.Token);
    }
}
