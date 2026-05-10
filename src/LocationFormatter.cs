using System.Collections.Frozen;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynQuery;

public static class LocationFormatter
{
    public static string? Format(
        FileLinePositionSpan span,
        bool context,
        SyntaxTree? tree,
        FrozenSet<string> documentPaths,
        string? basePath = null)
    {
        ArgumentNullException.ThrowIfNull(documentPaths);

        if (!string.IsNullOrEmpty(span.Path)
            && Path.IsPathRooted(span.Path)
            && !documentPaths.Contains(span.Path))
        {
            return null;
        }

        string path = basePath is not null
            ? Path.GetRelativePath(basePath, span.Path)
            : span.Path;
        string location = $"{path}:{span.StartLinePosition.Line + 1}";
        if (!context || tree is null)
        {
            return location;
        }

        SourceText text = tree.GetText();
        int lineNumber = span.StartLinePosition.Line;
        if (lineNumber >= text.Lines.Count)
        {
            return location;
        }

        string sourceLine = text.Lines[lineNumber].ToString().Trim();
        return $"{location}\t{sourceLine}";
    }
}
