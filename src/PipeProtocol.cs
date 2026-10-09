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

    internal static string GetStateDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            PidDirectoryName);

    internal static string PrepareStateDirectory()
    {
        string directory = GetStateDirectory();

        if (OperatingSystem.IsWindows())
        {
            // LocalApplicationData is already per-user and ACL-protected on Windows.
            Directory.CreateDirectory(directory);
        }
        else
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
