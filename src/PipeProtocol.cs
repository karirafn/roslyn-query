using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RoslynQuery;

/// <summary>Discriminator byte that leads every response frame in the new wire protocol.</summary>
/// <remarks>
/// Value 0 is reserved/invalid — an all-zero or truncated write from an old or dead daemon
/// produces a 0x00 first byte, which is rejected as <c>InvalidDataException</c> by
/// <see cref="PipeProtocol.ReadResponseEnvelopeAsync"/>, triggering the old-daemon kill+respawn path.
/// Values above 5 are also rejected as invalid.
/// </remarks>
#pragma warning disable CA1008 // zero member omitted intentionally — 0 is the reserved-invalid sentinel in the wire protocol
#pragma warning disable CA1028 // byte backing type required — the status discriminator is a single wire byte
public enum ResponseStatus : byte
{
    /// <summary>Normal completion. Body: [frame stdout][frame stderr][int32 exit].</summary>
    Result = 1,

    /// <summary>Daemon still loading the initial workspace. No body.</summary>
    Loading = 2,

    /// <summary>Daemon reloading after staleness detection. No body.</summary>
    Reloading = 3,

    /// <summary>Command threw an exception. Body: [frame message]. Exit is implicitly 1.</summary>
    Error = 4,

    /// <summary>
    /// Client protocol version does not match the daemon's. No body.
    /// The daemon exits after writing this status.
    /// </summary>
    VersionMismatch = 5,
}
#pragma warning restore CA1028
#pragma warning restore CA1008

public static class PipeProtocol
{
    internal const string Prefix = "roslyn-query-";
    private const int HashLength = 32;
    private const string PidDirectoryName = "roslyn-query";
    internal const int MaxFrameBytes = 64 * 1024 * 1024;

    /// <summary>Reserved sentinel arg array element that requests daemon shutdown over the pipe.</summary>
    /// <remarks>
    /// The client sends <c>["--shutdown"]</c>; the server intercepts it before dispatching to commands.
    /// Defined here so client and server share one literal.
    /// </remarks>
    internal const string ShutdownCommand = "--shutdown";

    /// <summary>
    /// Wire protocol version. Increment on any backward-incompatible change to request or response frames.
    /// Both client and daemon read this constant; the daemon compares the request's version against it
    /// and replies <see cref="ResponseStatus.VersionMismatch"/> on a discrepancy.
    /// </summary>
    public const int ProtocolVersion = 1;

    /// <summary>
    /// The assembly's informational version, with the SourceLink <c>+&lt;sha&gt;</c> suffix stripped.
    /// Carried in every request for diagnostics and version-mismatch reporting.
    /// </summary>
    public static string ClientInformationalVersion { get; } = ResolveInformationalVersion();

    private static string ResolveInformationalVersion()
    {
        string? raw = typeof(PipeProtocol)
            .Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (raw is null)
        {
            return "0.0.0";
        }

        int plusIndex = raw.IndexOf('+', StringComparison.Ordinal);
        return plusIndex >= 0 ? raw[..plusIndex] : raw;
    }

    // Windows and macOS use case-insensitive file systems: two paths that differ only
    // in case refer to the same file, so the hash must treat them identically.
    // Linux uses a case-sensitive file system: /a/App.sln and /a/APP.sln are distinct
    // files. Without this guard, both would map to the same pipe name and PID file,
    // letting a client for one solution attach to a daemon serving the other.
    // Known limitation: detection is by OS platform, so a case-sensitive macOS/APFS
    // volume (or a case-insensitive mount under Linux) is misclassified; a runtime
    // filesystem probe would be needed for exact detection.
    private static readonly bool CaseInsensitiveFileSystem =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public static string DerivePipeName(string solutionPath)
    {
        string hash = Hash(solutionPath);
        return $"{Prefix}{hash}";
    }

    public static string DerivePidFilePath(string solutionPath)
    {
        string hash = Hash(solutionPath);
        return Path.Combine(GetStateDirectory(), $"{Prefix}{hash}.pid");
    }

    // AsyncLocal rather than a plain static field: the value flows with each test's async
    // execution context, so parallel xUnit test classes do not stamp each other.
    private static readonly AsyncLocal<string?> s_stateDirectoryOverride = new();

    /// <summary>Sets the state-directory override for the current async context.</summary>
    /// <remarks>
    /// Test-isolation seam only. Pass <see langword="null"/> to clear.
    /// </remarks>
    internal static void SetStateDirectoryOverrideForTests(string? directory) =>
        s_stateDirectoryOverride.Value = directory;

    internal static string GetStateDirectory()
    {
        // 1. AsyncLocal test override — highest precedence; see SetStateDirectoryOverrideForTests.
        if (s_stateDirectoryOverride.Value is { Length: > 0 } overrideDir)
        {
            return overrideDir;
        }

        // 2. Environment variable — production override; lets CI/containers relocate daemon state
        //    onto a tmpfs or mounted volume (and is the mitigation for empty LocalApplicationData
        //    in minimal containers). The variable names the FINAL directory — no subfolder appended.
        string envVar = Environment.GetEnvironmentVariable("ROSLYN_QUERY_STATE_DIR") ?? string.Empty;
        if (envVar.Length > 0)
        {
            return envVar;
        }

        // 3. LocalApplicationData — the normal case on a developer workstation.
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (localAppData.Length > 0)
        {
            return Path.Combine(localAppData, PidDirectoryName);
        }

        // 4. Temp fallback — when LocalApplicationData is empty (no HOME in minimal containers).
        //    Per-user isolation via UserName prevents cross-user collisions and avoids returning
        //    a relative cwd path (which Path.GetTempPath() is guaranteed to be absolute).
        return Path.Combine(Path.GetTempPath(), $"{PidDirectoryName}-{Environment.UserName}");
    }

    internal static string PrepareStateDirectory()
    {
        string directory = GetStateDirectory();

        // Pre-check: refuse a symlink that was already in place before we try to create anything.
        // A symlinked state directory could redirect PID files to an attacker-controlled location.
        DirectoryInfo info = new(directory);
        if (info.Exists && info.LinkTarget is not null)
        {
            throw new IOException(
                $"State directory '{directory}' is a symbolic link. A symlinked state directory is refused.");
        }

        if (OperatingSystem.IsWindows())
        {
            // LocalApplicationData is already per-user and ACL-protected on Windows.
            Directory.CreateDirectory(directory);
        }
        else
        {
            // Create with 0700 — this only applies the mode when the directory is being created.
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // Post-create check: close the TOCTOU window on the temp-fallback path. Between the
            // pre-check above and CreateDirectory completing, an attacker on a world-writable temp
            // dir could have swapped in a symlink. Re-reading the DirectoryInfo catches that case.
            // (The primary defence is the 0700 directory inside the per-user home or temp path;
            // this re-check is the belt behind that suspender.)
            DirectoryInfo postInfo = new(directory);
            if (postInfo.LinkTarget is not null)
            {
                throw new IOException(
                    $"State directory '{directory}' is a symbolic link. A symlinked state directory is refused.");
            }

            // Re-tighten if the directory already existed with a looser mode, because
            // Directory.CreateDirectory only applies the mode on creation.
            UnixFileMode current = File.GetUnixFileMode(directory);
            if (current != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        return directory;
    }

    /// <summary>
    /// Writes a versioned request envelope: protocol-version int (raw big-endian int32),
    /// client informational version (length-prefixed UTF-8 frame), and args (length-prefixed UTF-8 JSON frame).
    /// </summary>
    public static async Task WriteRequestAsync(
        Stream stream,
        int protocolVersion,
        string clientVersion,
        string[] args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] versionBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(versionBytes, protocolVersion);
        await stream.WriteAsync(versionBytes, cancellationToken);
        await WriteFrameAsync(stream, Encoding.UTF8.GetBytes(clientVersion), cancellationToken);
        await WriteFrameAsync(stream, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(args)), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Reads a versioned request envelope. Returns the protocol version, client informational version, and args.
    /// </summary>
    public static async Task<(int ProtocolVersion, string ClientVersion, string[] Args)> ReadRequestEnvelopeAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] versionBytes = new byte[4];
        await stream.ReadExactlyAsync(versionBytes, cancellationToken);
        int protocolVersion = BinaryPrimitives.ReadInt32BigEndian(versionBytes);
        byte[] clientVersionBytes = await ReadFrameAsync(stream, cancellationToken);
        byte[] argsBytes = await ReadFrameAsync(stream, cancellationToken);
        string clientVersion = Encoding.UTF8.GetString(clientVersionBytes);
        string[] args = JsonSerializer.Deserialize<string[]>(argsBytes) ?? [];
        return (protocolVersion, clientVersion, args);
    }

    /// <summary>
    /// Writes a <see cref="ResponseStatus.Result"/> response envelope:
    /// status byte, then [frame stdout][frame stderr][int32 exit].
    /// </summary>
    public static async Task WriteResultAsync(
        Stream stream,
        string stdout,
        string stderr,
        int exit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.WriteByte((byte)ResponseStatus.Result);
        await WriteFrameAsync(stream, Encoding.UTF8.GetBytes(stdout), cancellationToken);
        await WriteFrameAsync(stream, Encoding.UTF8.GetBytes(stderr), cancellationToken);
        byte[] exitBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(exitBytes, exit);
        await stream.WriteAsync(exitBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Writes a status-only response envelope (no body): <see cref="ResponseStatus.Loading"/>,
    /// <see cref="ResponseStatus.Reloading"/>, or <see cref="ResponseStatus.VersionMismatch"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="status"/> is <see cref="ResponseStatus.Result"/> or
    /// <see cref="ResponseStatus.Error"/>, which carry a body and must use
    /// <see cref="WriteResultAsync"/> or <see cref="WriteErrorAsync"/> respectively.
    /// </exception>
    public static async Task WriteStatusAsync(
        Stream stream,
        ResponseStatus status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (status is ResponseStatus.Result or ResponseStatus.Error)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                $"{status} carries a body; use WriteResultAsync or WriteErrorAsync instead.");
        }

        stream.WriteByte((byte)status);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Writes an <see cref="ResponseStatus.Error"/> response envelope:
    /// status byte, then [frame message]. Exit is implicitly 1 on the client side.
    /// </summary>
    public static async Task WriteErrorAsync(
        Stream stream,
        string message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.WriteByte((byte)ResponseStatus.Error);
        await WriteFrameAsync(stream, Encoding.UTF8.GetBytes(message), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Reads a response envelope. Returns a <see cref="DaemonResponse"/> with the decoded status and body.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// Thrown when the status byte is 0 (reserved invalid), greater than 5 (unknown), or when EOF is
    /// reached before the status byte can be read (old-daemon / short-reply detector).
    /// </exception>
    public static async Task<DaemonResponse> ReadResponseEnvelopeAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] statusBuffer = new byte[1];
        try
        {
            await stream.ReadExactlyAsync(statusBuffer, cancellationToken);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException(
                "Unexpected end of stream before the status byte could be read. " +
                "The daemon may be running an old protocol version or have exited unexpectedly.", ex);
        }

        byte statusByte = statusBuffer[0];
        if (statusByte == 0 || statusByte > 5)
        {
            throw new InvalidDataException(
                $"Invalid response status byte {statusByte}. " +
                "Expected a value in [1, 5]. The daemon may be running an old protocol version.");
        }

        ResponseStatus status = (ResponseStatus)statusByte;

        return status switch
        {
            ResponseStatus.Result => await ReadResultBodyAsync(stream, cancellationToken),
            ResponseStatus.Loading => new DaemonResponse(ResponseStatus.Loading, "", "", 0, ""),
            ResponseStatus.Reloading => new DaemonResponse(ResponseStatus.Reloading, "", "", 0, ""),
            ResponseStatus.VersionMismatch => new DaemonResponse(ResponseStatus.VersionMismatch, "", "", 0, ""),
            ResponseStatus.Error => await ReadErrorBodyAsync(stream, cancellationToken),
            _ => throw new UnreachableException($"Unhandled ResponseStatus value: {status}"),
        };
    }

    /// <summary>
    /// Reads the body of a <see cref="ResponseStatus.Result"/> response after the status byte
    /// has already been consumed. Returns a <see cref="DaemonResponse"/> with
    /// <see cref="ResponseStatus.Result"/> and the decoded stdout/stderr/exit fields.
    /// </summary>
    internal static async Task<DaemonResponse> ReadResultBodyAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] stdoutBytes = await ReadFrameAsync(stream, cancellationToken);
        byte[] stderrBytes = await ReadFrameAsync(stream, cancellationToken);
        byte[] exitBytes = new byte[4];
        await stream.ReadExactlyAsync(exitBytes, cancellationToken);
        return new DaemonResponse(
            ResponseStatus.Result,
            Encoding.UTF8.GetString(stdoutBytes),
            Encoding.UTF8.GetString(stderrBytes),
            BinaryPrimitives.ReadInt32BigEndian(exitBytes),
            "");
    }

    /// <summary>
    /// Reads the body of an <see cref="ResponseStatus.Error"/> response after the status byte
    /// has already been consumed. Returns a <see cref="DaemonResponse"/> with
    /// <see cref="ResponseStatus.Error"/> and the decoded error message.
    /// </summary>
    internal static async Task<DaemonResponse> ReadErrorBodyAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] messageBytes = await ReadFrameAsync(stream, cancellationToken);
        return new DaemonResponse(
            ResponseStatus.Error,
            "",
            "",
            1,
            Encoding.UTF8.GetString(messageBytes));
    }

    private static string Hash(string solutionPath)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(solutionPath));
        string normalised = CaseInsensitiveFileSystem ? fullPath.ToUpperInvariant() : fullPath;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexStringLower(hash)[..HashLength];
    }

    private static async Task WriteFrameAsync(
        Stream stream,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        byte[] lenBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBytes, payload.Length);
        await stream.WriteAsync(lenBytes, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    internal static async Task<byte[]> ReadFrameAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] lenBytes = new byte[4];
        await stream.ReadExactlyAsync(lenBytes, cancellationToken);
        int length = BinaryPrimitives.ReadInt32BigEndian(lenBytes);
        if (length < 0 || length > MaxFrameBytes)
        {
            throw new InvalidDataException(
                $"Frame length {length} is outside the allowed range [0, {MaxFrameBytes}].");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }
}
