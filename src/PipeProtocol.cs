using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace RoslynQuery;

public static class PipeProtocol
{
    private const string Prefix = "roslyn-query-";
    public const string StdoutEndSentinel = "---END---";
    public const string StderrEndSentinel = "---EXIT---";

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

    public static async Task WriteRequestAsync(PipeStream stream, string[] args)
    {
        ArgumentNullException.ThrowIfNull(stream);
        string line = string.Join('\t', args) + '\n';
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    public static async Task<string[]> ReadRequestAsync(
        PipeStream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        string line = await ReadLineAsync(stream, cancellationToken);
        return line.Split('\t');
    }

    public static async Task WriteResponseAsync(
        PipeStream stream,
        string stdout,
        string stderr,
        int exitCode)
    {
        ArgumentNullException.ThrowIfNull(stream);
        StringBuilder sb = new();

        if (!string.IsNullOrEmpty(stdout))
        {
            foreach (string line in stdout.Split('\n'))
            {
                sb.Append(line).Append('\n');
            }
        }

        sb.Append(StdoutEndSentinel).Append('\n');

        if (!string.IsNullOrEmpty(stderr))
        {
            foreach (string line in stderr.Split('\n'))
            {
                sb.Append(line).Append('\n');
            }
        }

        sb.Append(StderrEndSentinel).Append('\n');
        sb.Append(exitCode).Append('\n');

        byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    public static async Task<(string Stdout, string Stderr, int ExitCode)> ReadResponseAsync(
        PipeStream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        List<string> stdoutLines = [];
        while (true)
        {
            string line = await ReadLineAsync(stream, cancellationToken);
            if (line == StdoutEndSentinel)
            {
                break;
            }

            stdoutLines.Add(line);
        }

        List<string> stderrLines = [];
        while (true)
        {
            string line = await ReadLineAsync(stream, cancellationToken);
            if (line == StderrEndSentinel)
            {
                break;
            }

            stderrLines.Add(line);
        }

        string exitCodeLine = await ReadLineAsync(stream, cancellationToken);
        int exitCode = int.Parse(exitCodeLine, CultureInfo.InvariantCulture);

        string stdout = string.Join('\n', stdoutLines);
        string stderr = string.Join('\n', stderrLines);

        return (stdout, stderr, exitCode);
    }

#pragma warning disable CA5351 // MD5 is used for pipe name derivation, not cryptographic security
    private static string Hash(string solutionPath)
    {
        string normalised = Path.GetFullPath(solutionPath).ToUpperInvariant();
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(normalised));
        return Convert.ToHexStringLower(hash);
    }
#pragma warning restore CA5351

    private static async Task<string> ReadLineAsync(
        PipeStream stream,
        CancellationToken cancellationToken)
    {
        List<byte> buffer = [];
        byte[] singleByte = new byte[1];

        while (true)
        {
            int bytesRead = await stream.ReadAsync(singleByte, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            if (singleByte[0] == (byte)'\n')
            {
                break;
            }

            buffer.Add(singleByte[0]);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
