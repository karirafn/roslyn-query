using System.IO.Pipes;

namespace RoslynQuery;

public static class DaemonClient
{
    private static readonly TimeSpan DefaultHandshakeDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultResponseDeadline = TimeSpan.FromSeconds(75);
    private const int ShutdownConnectionTimeoutMs = 2000;

    // AsyncLocal seams mirror PipeProtocol.SetStateDirectoryOverrideForTests —
    // values flow with each test's async context so parallel test classes do not interfere.
    private static readonly AsyncLocal<TimeSpan?> s_handshakeDeadlineOverride = new();
    private static readonly AsyncLocal<TimeSpan?> s_responseDeadlineOverride = new();

    private static TimeSpan HandshakeDeadline =>
        s_handshakeDeadlineOverride.Value ?? DefaultHandshakeDeadline;

    private static TimeSpan ResponseDeadline =>
        s_responseDeadlineOverride.Value ?? DefaultResponseDeadline;

    // CurrentUserOnly is used on all platforms. On Unix the runtime compares the connecting
    // peer's effective uid with ours, so a socket another user pre-created at the pipe path
    // is refused before any request is read. On Windows the runtime reads the connected
    // pipe's owner SID and compares it against WindowsIdentity.GetCurrent().Owner; the
    // daemon server (CreatePipeServer) sets the same SID as owner explicitly, so legitimate
    // same-user, same-elevation connections pass and an impostor pipe owned by another user
    // is refused. A same-user cross-elevation connection is refused because .Owner differs
    // (BUILTIN\Administrators vs user SID) — that is acceptable: TryExecuteAsync catches
    // the resulting exception and returns NotRunning/MalformedOrShort, so the caller spawns
    // a daemon and does a direct run.
    private const PipeOptions ClientPipeOptions =
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;

    /// <summary>Overrides the handshake deadline for the current async context (tests only).</summary>
    internal static void SetHandshakeDeadlineForTests(TimeSpan? deadline) =>
        s_handshakeDeadlineOverride.Value = deadline;

    /// <summary>Overrides the response deadline for the current async context (tests only).</summary>
    internal static void SetResponseDeadlineForTests(TimeSpan? deadline) =>
        s_responseDeadlineOverride.Value = deadline;

#pragma warning disable CA1031 // Catch-all is by design: any failure is classified into a DaemonOutcome
    public static async Task<DaemonAttempt> TryExecuteAsync(
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
            // Phase 1: connect under the handshake deadline (connect + status-byte read)
            using CancellationTokenSource handshakeCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeCts.CancelAfter(HandshakeDeadline);

            NamedPipeClientStream pipe = new(
                ".",
                pipeName,
                PipeDirection.InOut,
                ClientPipeOptions);

            await using (pipe.ConfigureAwait(false))
            {
                try
                {
                    // Try to connect within the handshake window.
                    // NamedPipeClientStream.ConnectAsync with a timeout of 0 returns immediately
                    // if not connected. We use the CancellationToken to enforce our deadline.
                    await pipe.ConnectAsync(handshakeCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    ex is TimeoutException
                    or IOException
                    or UnauthorizedAccessException
                    or OperationCanceledException
                    or System.ComponentModel.Win32Exception)
                {
                    // If the external cancellation token is cancelled, propagate
                    cancellationToken.ThrowIfCancellationRequested();
                    // If the pipe does not exist, return NotRunning
                    return new DaemonAttempt(DaemonOutcome.NotRunning, 0);
                }

                // Write the versioned request
                await PipeProtocol.WriteRequestAsync(
                        pipe,
                        PipeProtocol.ProtocolVersion,
                        PipeProtocol.ClientInformationalVersion,
                        args,
                        handshakeCts.Token)
                    .ConfigureAwait(false);

                // Read the status byte under the handshake deadline
                DaemonResponse response;
                try
                {
                    response = await ReadStatusByteAsync(pipe, handshakeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Handshake deadline fired — daemon is wedged
                    return new DaemonAttempt(DaemonOutcome.Wedged, 0);
                }
                catch (Exception ex) when (
                    ex is InvalidDataException
                    or IOException
                    or EndOfStreamException)
                {
                    // Malformed or short frame while PID may be alive → caller kills+respawns
                    return new DaemonAttempt(DaemonOutcome.MalformedOrShort, 0);
                }

                // Handshake succeeded: branch on status
                switch (response.Status)
                {
                    case ResponseStatus.Loading:
                        return new DaemonAttempt(DaemonOutcome.Loading, 0);

                    case ResponseStatus.Reloading:
                        return new DaemonAttempt(DaemonOutcome.Reloading, 0);

                    case ResponseStatus.VersionMismatch:
                        // Old/mismatched daemon — treat same as malformed, caller replaces it
                        return new DaemonAttempt(DaemonOutcome.MalformedOrShort, 0);

                    case ResponseStatus.Error:
                        // Error status: daemon already wrote the message in the body (ErrorMessage).
                        // Write to stderr and return as a Result (no retry).
                        await stderr.WriteLineAsync(response.ErrorMessage).ConfigureAwait(false);
                        return new DaemonAttempt(DaemonOutcome.Result, 1);

                    case ResponseStatus.Result:
                        // Status byte was valid (Result), now read the body under the response deadline
                        break;

                    default:
                        return new DaemonAttempt(DaemonOutcome.MalformedOrShort, 0);
                }

                // Phase 2: read the response body under the response deadline
                using CancellationTokenSource responseCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                responseCts.CancelAfter(ResponseDeadline);

                DaemonResponse fullResponse;
                try
                {
                    fullResponse = await PipeProtocol.ReadResultBodyAsync(pipe, responseCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Response deadline exceeded
                    return new DaemonAttempt(DaemonOutcome.ResponseTimeout, 0);
                }

                if (!string.IsNullOrEmpty(fullResponse.Stderr))
                {
                    await stderr.WriteAsync(fullResponse.Stderr).ConfigureAwait(false);
                }

                if (!string.IsNullOrEmpty(fullResponse.Stdout))
                {
                    await stdout.WriteAsync(fullResponse.Stdout).ConfigureAwait(false);
                }

                return new DaemonAttempt(DaemonOutcome.Result, fullResponse.ExitCode);
            }
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new DaemonAttempt(DaemonOutcome.NotRunning, 0);
        }
    }
#pragma warning restore CA1031

#pragma warning disable CA1031 // Catch-all is by design: any failure returns false to signal daemon unreachable
    public static async Task<bool> TryRequestShutdownAsync(
        string solutionPath,
        CancellationToken cancellationToken = default)
    {
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
                await pipe
                    .ConnectAsync(ShutdownConnectionTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);

                await PipeProtocol.WriteRequestAsync(
                        pipe,
                        PipeProtocol.ProtocolVersion,
                        PipeProtocol.ClientInformationalVersion,
                        [PipeProtocol.ShutdownCommand],
                        cancellationToken)
                    .ConfigureAwait(false);

                DaemonResponse response = await PipeProtocol.ReadResponseEnvelopeAsync(
                        pipe,
                        cancellationToken)
                    .ConfigureAwait(false);

                return response.Status == ResponseStatus.Result && response.ExitCode == 0;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }
#pragma warning restore CA1031

    // Reads only the status byte (plus any no-body status payload) from the pipe.
    // For Result status: returns a DaemonResponse with Status=Result and empty body
    // (caller must then read the body under the response deadline). For other statuses: returns full response.
    private static async Task<DaemonResponse> ReadStatusByteAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] statusBuffer = new byte[1];

        try
        {
            await stream.ReadExactlyAsync(statusBuffer, cancellationToken);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException(
                "Unexpected end of stream before the status byte could be read.", ex);
        }

        byte statusByte = statusBuffer[0];
        if (statusByte == 0 || statusByte > 5)
        {
            throw new InvalidDataException(
                $"Invalid response status byte {statusByte}. Expected a value in [1, 5].");
        }

        ResponseStatus status = (ResponseStatus)statusByte;

        return status switch
        {
            ResponseStatus.Result => new DaemonResponse(ResponseStatus.Result, "", "", 0, ""),
            ResponseStatus.Loading => new DaemonResponse(ResponseStatus.Loading, "", "", 0, ""),
            ResponseStatus.Reloading => new DaemonResponse(ResponseStatus.Reloading, "", "", 0, ""),
            ResponseStatus.VersionMismatch => new DaemonResponse(ResponseStatus.VersionMismatch, "", "", 0, ""),
            ResponseStatus.Error => await PipeProtocol.ReadErrorBodyAsync(stream, cancellationToken),
            _ => throw new System.Diagnostics.UnreachableException($"Unhandled ResponseStatus: {status}"),
        };
    }
}
