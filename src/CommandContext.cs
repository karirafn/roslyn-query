using Microsoft.CodeAnalysis;

namespace RoslynQuery;

public sealed class CommandContext(TextWriter stdout, TextWriter stderr, Solution solution)
{
    public TextWriter Stdout { get; } = stdout;
    public TextWriter Stderr { get; } = stderr;
    public Solution Solution { get; } = solution;
}
