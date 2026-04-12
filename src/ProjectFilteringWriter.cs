using System.Runtime.InteropServices;
using System.Text;

namespace RoslynQuery;

/// <summary>
/// Wraps a TextWriter and passes through only lines whose file path
/// is contained within the specified project directory.
/// Output lines that cannot be parsed as file:line format are filtered out.
/// </summary>
public sealed class ProjectFilteringWriter : TextWriter
{
    private static readonly StringComparison PathComparison =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

    private readonly TextWriter _inner;
    private readonly string _projectDirectory;
    private readonly string _solutionDirectory;

    public ProjectFilteringWriter(
        TextWriter inner,
        string projectDirectory,
        string solutionDirectory)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(projectDirectory);
        ArgumentNullException.ThrowIfNull(solutionDirectory);

        _inner = inner;
        _projectDirectory = projectDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        _solutionDirectory = solutionDirectory;
    }

    public override Encoding Encoding => _inner.Encoding;

    // All command output uses WriteLineAsync(string?).
    // Override the sync and ReadOnlyMemory overloads as well so that
    // no write path can bypass the filter.
    public override void WriteLine(string? value)
    {
        if (value is not null && IsInProject(value))
        {
            _inner.WriteLine(value);
        }
    }

    public override async Task WriteLineAsync(string? value)
    {
        if (value is not null && IsInProject(value))
        {
            await _inner.WriteLineAsync(value);
        }
    }

    public override async Task WriteLineAsync(
        ReadOnlyMemory<char> buffer,
        CancellationToken cancellationToken = default)
    {
        string value = buffer.ToString();
        if (IsInProject(value))
        {
            await _inner.WriteLineAsync(buffer, cancellationToken);
        }
    }

    private bool IsInProject(string line)
    {
        string? filePath = ExtractFilePath(line);
        if (filePath is null)
        {
            return false;
        }

        string absolutePath = Path.IsPathRooted(filePath)
            ? filePath
            : Path.GetFullPath(filePath, _solutionDirectory);

        return absolutePath.StartsWith(_projectDirectory, PathComparison);
    }

    /// <summary>
    /// Extracts the file path from a command output line.
    /// Supports all output formats:
    ///   path:line                       (simple)
    ///   path:line&lt;tab&gt;something          (with context or symbol)
    ///   kind&lt;tab&gt;type&lt;tab&gt;path:line      (list-types)
    /// Returns null if no path:line field is found (e.g. "# Symbol" headers).
    /// </summary>
    public static string? ExtractFilePath(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        // Scan each tab-separated field for the first one matching path:digits.
        // The file:line field is the first field matching this pattern in all
        // current output formats (including list-types where it is the last field).
        int fieldStart = 0;

        while (fieldStart <= line.Length)
        {
            int tabIdx = line.IndexOf('\t', fieldStart);
            int fieldEnd = tabIdx >= 0 ? tabIdx : line.Length;

            string field = line[fieldStart..fieldEnd];
            int colonIdx = field.LastIndexOf(':');

            if (colonIdx > 0)
            {
                string afterColon = field[(colonIdx + 1)..];
                if (afterColon.Length > 0 && afterColon.All(char.IsAsciiDigit))
                {
                    return field[..colonIdx];
                }
            }

            if (tabIdx < 0)
            {
                break;
            }

            fieldStart = tabIdx + 1;
        }

        return null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
