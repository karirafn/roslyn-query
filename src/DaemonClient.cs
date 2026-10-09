using System.Diagnostics;
using System.IO.Pipes;

namespace RoslynQuery;

public static class DaemonClient
{
    private const int ConnectionTimeoutMs = 2000;
    private const int TransientExitCode = 75;

    // On Unix, CurrentUserOnly makes ConnectAsync refuse a server whose effective uid
    // differs from ours, so a socket another user pre-created at our pipe path is never
    // trusted. Windows is excluded: there the check compares the pipe's owner SID with
    // the token's default owner, which is BUILTIN\Administrators in an elevated process,
    // so a terminal whose elevation differs from the daemon's would be refused.
    private static readonly PipeOptions ClientPipeOptions = OperatingSystem.IsWindows()
        ? PipeOptions.Asynchronous
        : PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

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
