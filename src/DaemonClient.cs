using System.Diagnostics;
using System.IO.Pipes;

namespace RoslynQuery;

public static class DaemonClient
{
    private const int ConnectionTimeoutMs = 2000;
    private const int TransientExitCode = 75;

    // CurrentUserOnly is used on all platforms. On Unix the runtime compares the connecting
    // peer's effective uid with ours, so a socket another user pre-created at the pipe path
    // is refused before any request is read. On Windows the runtime reads the connected
    // pipe's owner SID and compares it against WindowsIdentity.GetCurrent().Owner; the
    // daemon server (CreatePipeServer) sets the same SID as owner explicitly, so legitimate
    // same-user, same-elevation connections pass and an impostor pipe owned by another user
    // is refused. A same-user cross-elevation connection is refused because .Owner differs
    // (BUILTIN\Administrators vs user SID) — that is acceptable: TryExecuteAsync catches
    // the resulting exception and returns daemon-unavailable, so the caller falls back to a
    // direct in-process run (correct result, lost speed-up only).
    private const PipeOptions ClientPipeOptions =
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

#pragma warning disable CA1031 // Catch-all is by design: any failure returns null to signal daemon unavailability
    public static async Task<(int? ExitCode, bool WasReloading)> TryExecuteAsync(
        string solutionPath,
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        string pipeName = PipeProtocol.DerivePipeName(solutionPath);

        try
        {
            NamedPipeClientStream pipe = new(
                ".",
                pipeName,
                PipeDirection.InOut,
                ClientPipeOptions);

            await using (pipe.ConfigureAwait(false))
            {
                await pipe.ConnectAsync(ConnectionTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);

                await PipeProtocol.WriteRequestAsync(pipe, args, cancellationToken)
                    .ConfigureAwait(false);

                (string stdoutContent, string stderrContent, int exitCode) =
                    await PipeProtocol.ReadResponseAsync(pipe, cancellationToken)
                        .ConfigureAwait(false);

                if (!string.IsNullOrEmpty(stderrContent))
                {
                    await stderr.WriteAsync(stderrContent).ConfigureAwait(false);
                }

                if (exitCode == TransientExitCode)
                {
                    Debug.Assert(
                        string.IsNullOrEmpty(stdoutContent),
                        "Transient reload response unexpectedly contained stdout");
                    return (null, WasReloading: true);
                }

                if (!string.IsNullOrEmpty(stdoutContent))
                {
                    await stdout.WriteAsync(stdoutContent).ConfigureAwait(false);
                }

                return (exitCode, WasReloading: false);
            }
        }
        catch (Exception)
        {
            return (null, WasReloading: false);
        }
    }
#pragma warning restore CA1031
}
