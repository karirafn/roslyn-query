using Microsoft.CodeAnalysis;

namespace RoslynQuery;

internal sealed record UnusedSymbolMatch(
    string Path,
    int Line,
    string DisplayName,
    FileLinePositionSpan Span,
    SyntaxTree? Tree);
