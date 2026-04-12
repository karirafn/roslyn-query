using System.Text;

namespace RoslynQuery;

/// <summary>
/// Wraps a TextWriter and passes through only lines whose file path
/// is contained within the specified project directory.
/// </summary>
public sealed class ProjectFilteringWriter : TextWriter
{
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

    public override async Task WriteLineAsync(string? value)
    {
        if (value is not null && IsInProject(value))
        {
            await _inner.WriteLineAsync(value);
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

        return absolutePath.StartsWith(_projectDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractFilePath(string line)
    {
        int tabIdx = line.IndexOf('\t', StringComparison.Ordinal);
        string fileAndLine = tabIdx >= 0 ? line[..tabIdx] : line;

        int lastColon = fileAndLine.LastIndexOf(':');
        if (lastColon < 0)
        {
            return null;
        }

        string afterColon = fileAndLine[(lastColon + 1)..];
        if (afterColon.Length == 0 || !IsAllDigits(afterColon))
        {
            return null;
        }

        return fileAndLine[..lastColon];
    }

    private static bool IsAllDigits(string s) =>
        s.All(char.IsAsciiDigit);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
