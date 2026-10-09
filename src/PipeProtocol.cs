using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RoslynQuery;

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

        // Refuse symlinks before creating or using the directory — a symlinked state directory
        // could redirect PID files to an attacker-controlled location.
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

    public static async Task WriteRequestAsync(
        Stream stream,
        string[] args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(args));
        await WriteFrameAsync(stream, payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<string[]> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] payload = await ReadFrameAsync(stream, cancellationToken);
        return JsonSerializer.Deserialize<string[]>(payload) ?? [];
    }

    public static async Task WriteResponseAsync(
        Stream stream,
        string stdout,
        string stderr,
        int exitCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await WriteFrameAsync(stream, Encoding.UTF8.GetBytes(stdout), cancellationToken);
        await WriteFrameAsync(stream, Encoding.UTF8.GetBytes(stderr), cancellationToken);
        byte[] exitBytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(exitBytes, exitCode);
        await stream.WriteAsync(exitBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<(string Stdout, string Stderr, int ExitCode)> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] stdoutBytes = await ReadFrameAsync(stream, cancellationToken);
        byte[] stderrBytes = await ReadFrameAsync(stream, cancellationToken);
        byte[] exitBytes = new byte[4];
        await stream.ReadExactlyAsync(exitBytes, cancellationToken);
        return (
            Encoding.UTF8.GetString(stdoutBytes),
            Encoding.UTF8.GetString(stderrBytes),
            BinaryPrimitives.ReadInt32BigEndian(exitBytes));
    }

    private static string Hash(string solutionPath)
    {
        string normalised = Path.GetFullPath(solutionPath).ToUpperInvariant();
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

    private static async Task<byte[]> ReadFrameAsync(
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
