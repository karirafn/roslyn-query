using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace RoslynQuery;

public static class DaemonServer
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    private const int TransientExitCode = 75;

    public static async Task RunAsync(
        string solutionPath,
        CancellationToken cancellationToken = default)
    {
        solutionPath = Path.GetFullPath(solutionPath);
        string pipeName = PipeProtocol.DerivePipeName(solutionPath);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            DaemonProcess.CleanupPidFile(solutionPath);

        using MSBuildWorkspace workspace = MSBuildWorkspace.Create();
        await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
        Solution solution = workspace.CurrentSolution;
        DateTime lastWriteTime = File.GetLastWriteTimeUtc(solutionPath);
        bool reloading = false;

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

                    DateTime currentWriteTime = File.GetLastWriteTimeUtc(solutionPath);
                    if (currentWriteTime > lastWriteTime)
                    {
                        await PipeProtocol.WriteResponseAsync(
                            pipe,
                            "",
                            "daemon: workspace reloading",
                            TransientExitCode,
                            linkedCts.Token);
                        pipe.Disconnect();

                        if (!reloading)
                        {
                            reloading = true;
                            _ = Task.Run(
                                async () =>
                                {
                                    try
                                    {
                                        await workspace.OpenSolutionAsync(
                                            solutionPath,
                                            cancellationToken: cancellationToken);
                                        solution = workspace.CurrentSolution;
                                        lastWriteTime =
                                            File.GetLastWriteTimeUtc(solutionPath);
                                    }
                                    finally
                                    {
                                        reloading = false;
                                    }
                                },
                                cancellationToken);
                        }

                        continue;
                    }

                    StringWriter stdoutWriter = new();
                    StringWriter stderrWriter = new();
                    CommandContext context = new(stdoutWriter, stderrWriter, solution);

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
