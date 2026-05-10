using System.Collections.Frozen;

using Microsoft.CodeAnalysis;

namespace RoslynQuery;

public sealed class CommandContext(
    TextWriter stdout,
    TextWriter stderr,
    Solution solution,
    string solutionDirectory = "",
    FrozenSet<string>? documentPaths = null)
{
    public TextWriter Stdout { get; } = stdout;
    public TextWriter Stderr { get; } = stderr;
    public Solution Solution { get; } = solution;
    public string SolutionDirectory { get; } = solutionDirectory;
    public FrozenSet<string> DocumentPaths { get; } = documentPaths ?? FrozenSet<string>.Empty;
}
