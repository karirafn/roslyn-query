using System.Collections.Frozen;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynQuery;

public static class DaemonServer
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StalenessCheckInterval = TimeSpan.FromSeconds(2);
    private const int TransientExitCode = 75;
    private const int QueryTimeoutSeconds = 60;

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
        await SolutionLoader.LoadAsync(workspace, solutionPath, cancellationToken);
        IReadOnlyList<string> initialTrackedPaths = TrackedFiles.CollectPaths(
            workspace.CurrentSolution,
            solutionDirectory,
            solutionPath);
        FrozenSet<string> initialDocumentPaths =
            TrackedFiles.CollectDocumentPaths(workspace.CurrentSolution);
        ReloadState reloadState = new(workspace.CurrentSolution, initialTrackedPaths, initialDocumentPaths);
        DateTime lastStalenessCheck = DateTime.MinValue;

        CancellationTokenSource idleCts = new(IdleTimeout);
        CancellationTokenSource linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idleCts.Token);

        try
        {
            while (!linkedCts.Token.IsCancellationRequested)
            {
                try
                {
#pragma warning disable CA2000 // False positive: pipe is disposed via await using
                    await using NamedPipeServerStream pipe = CreatePipeServer(pipeName);
#pragma warning restore CA2000

                    await pipe.WaitForConnectionAsync(linkedCts.Token);

                    ResetIdleTimer(ref idleCts, ref linkedCts, cancellationToken);

                    string[] args = await PipeProtocol.ReadRequestAsync(
                        pipe,
                        linkedCts.Token);

                    if (args is [PipeProtocol.ShutdownCommand])
                    {
                        await PipeProtocol.WriteResponseAsync(pipe, "", "", 0, linkedCts.Token);
                        pipe.Disconnect();
                        break;
                    }

                    bool stale = false;
                    if (DateTime.UtcNow - lastStalenessCheck >= StalenessCheckInterval)
                    {
                        lastStalenessCheck = DateTime.UtcNow;
                        stale = await reloadState.IsStaleAsync();
                    }

                    if (stale)
                    {
                        await PipeProtocol.WriteResponseAsync(
                            pipe,
                            "",
                            "daemon: workspace reloading",
                            TransientExitCode,
                            linkedCts.Token);
                        pipe.Disconnect();

                        if (reloadState.TryBeginReload())
                        {
                            _ = Task.Run(
                                async () =>
                                {
                                    try
                                    {
                                        await SolutionLoader.LoadAsync(
                                            workspace,
                                            solutionPath,
                                            cancellationToken);
                                        IReadOnlyList<string> reloadedPaths = TrackedFiles.CollectPaths(
                                            workspace.CurrentSolution,
                                            solutionDirectory,
                                            solutionPath);
                                        FrozenSet<string> reloadedDocumentPaths =
                                            TrackedFiles.CollectDocumentPaths(workspace.CurrentSolution);
                                        reloadState.CompleteReload(
                                            workspace.CurrentSolution,
                                            reloadedPaths,
                                            reloadedDocumentPaths);
                                    }
#pragma warning disable CA1031 // Abort reload on any failure to avoid stuck state
                                    catch
#pragma warning restore CA1031
                                    {
                                        reloadState.AbortReload();
                                    }
                                },
                                cancellationToken);
                        }

                        continue;
                    }

                    StringWriter stdoutWriter = new();
                    StringWriter stderrWriter = new();
                    CommandContext context = new(
                        stdoutWriter,
                        stderrWriter,
                        reloadState.Solution,
                        solutionDirectory,
                        reloadState.DocumentPaths);

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
                    finally
                    {
                        queryLinkedCts.Dispose();
                        queryCts.Dispose();
                    }

                    if (queryTimedOut)
                    {
                        await PipeProtocol.WriteResponseAsync(
                            pipe,
                            "",
                            $"query timed out after {QueryTimeoutSeconds} seconds",
                            1,
                            linkedCts.Token);
                        pipe.Disconnect();
                        continue;
                    }

                    await PipeProtocol.WriteResponseAsync(
                        pipe,
                        stdoutWriter.ToString(),
                        stderrWriter.ToString(),
                        exitCode,
                        linkedCts.Token);

                    pipe.Disconnect();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
#pragma warning disable CA1031 // Catch general exception to keep the daemon alive
                catch (Exception) when (!linkedCts.Token.IsCancellationRequested)
#pragma warning restore CA1031
                {
                    // Protocol errors (e.g. InvalidDataException from frame guard,
                    // IOException from broken pipe) and UnauthorizedAccessException from
                    // WaitForConnectionAsync when a peer with a different uid connects should
                    // not crash the daemon. The pipe is disposed by the await using, so just
                    // continue to accept the next connection.
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
                maxNumberOfServerInstances: 1,
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
            1,
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
