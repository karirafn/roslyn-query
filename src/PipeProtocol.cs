using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RoslynQuery;

public static class PipeProtocol
{
    private const string Prefix = "roslyn-query-";
    internal const int MaxFrameBytes = 64 * 1024 * 1024;

    public static string DerivePipeName(string solutionPath)
    {
        string hash = Hash(solutionPath);
        return $"{Prefix}{hash}";
    }

    public static string DerivePidFilePath(string solutionPath)
    {
        string hash = Hash(solutionPath);
        return Path.Combine(Path.GetTempPath(), $"{Prefix}{hash}.pid");
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

#pragma warning disable CA5351 // MD5 is used for pipe name derivation, not cryptographic security
    private static string Hash(string solutionPath)
    {
        string normalised = Path.GetFullPath(solutionPath).ToUpperInvariant();
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexStringLower(hash);
    }
#pragma warning restore CA5351

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
