using System.IO.Pipes;

namespace RoslynQuery;

public static class DaemonClient
{
    private const int ConnectionTimeoutMs = 2000;

#pragma warning disable CA1031 // Catch-all is by design: any failure returns null to signal daemon unavailability
    public static async Task<int?> TryExecuteAsync(
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
                PipeOptions.Asynchronous);

            await using (pipe.ConfigureAwait(false))
            {
                await pipe.ConnectAsync(ConnectionTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);

                await PipeProtocol.WriteRequestAsync(pipe, args, cancellationToken)
                    .ConfigureAwait(false);

                (string stdoutContent, string stderrContent, int exitCode) =
                    await PipeProtocol.ReadResponseAsync(pipe, cancellationToken)
                        .ConfigureAwait(false);

                if (!string.IsNullOrEmpty(stdoutContent))
                {
                    await stdout.WriteAsync(stdoutContent).ConfigureAwait(false);
                }

                if (!string.IsNullOrEmpty(stderrContent))
                {
                    await stderr.WriteAsync(stderrContent).ConfigureAwait(false);
                }

                return exitCode;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }
#pragma warning restore CA1031
}
