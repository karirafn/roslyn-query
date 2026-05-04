using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynQuery;

public static class DaemonServer
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    private const int TransientExitCode = 75;
    private const uint OwnerOnlyUmask = 0x7F; // 0177 octal — restricts group/other r/w/x

#pragma warning disable CA5392 // P/Invoke targets libc — DefaultDllImportSearchPath does not apply
    [DllImport("libc", SetLastError = false)]
    private static extern uint umask(uint mask);
#pragma warning restore CA5392

    public static uint ApplyUnixPipeUmask()
    {
        if (!OperatingSystem.IsWindows())
        {
            return umask(OwnerOnlyUmask);
        }

        return 0;
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
        await SolutionLoader.LoadAsync(workspace, solutionPath, cancellationToken);
        IReadOnlyList<string> initialTrackedPaths = TrackedFiles.CollectPaths(
            workspace.CurrentSolution,
            solutionDirectory,
            solutionPath);
        ReloadState reloadState = new(workspace.CurrentSolution, initialTrackedPaths);

        ApplyUnixPipeUmask();

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

                    if (reloadState.IsStale())
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
                                        reloadState.CompleteReload(
                                            workspace.CurrentSolution,
                                            reloadedPaths);
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
                        solutionDirectory);

                    int exitCode = await CommandDispatcher.ExecuteAsync(args, context);

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
                    // IOException from broken pipe) should not crash the daemon.
                    // The pipe is disposed by the await using, so just continue
                    // to accept the next connection.
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
            PipeSecurity security = new();
            security.AddAccessRule(new PipeAccessRule(
                WindowsIdentity.GetCurrent().User!,
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

        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
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
