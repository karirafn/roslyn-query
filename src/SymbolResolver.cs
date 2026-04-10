using Microsoft.CodeAnalysis;

namespace RoslynQuery;

public sealed record SymbolResolverResult(IReadOnlyList<ISymbol> Symbols, int ExitCode);

public static class SymbolResolver
{
    public static SymbolResolverResult ResolveOrAll(
        IReadOnlyList<ISymbol> candidates,
        string symbolName,
        bool all,
        TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(symbolName);
        ArgumentNullException.ThrowIfNull(stderr);

        if (candidates.Count == 0)
        {
            if (!all)
            {
                stderr.WriteLine($"error: Symbol not found: {symbolName}");
            }

            return new SymbolResolverResult([], all ? 0 : 1);
        }

        if (candidates.Count > 1 && !all)
        {
            stderr.WriteLine($"Ambiguous '{symbolName}' — {candidates.Count} matches:");
            foreach (ISymbol s in candidates)
            {
                stderr.WriteLine($"  {s.ToDisplayString()} ({s.Kind})");
            }
            stderr.WriteLine("Use TypeName.MemberName to disambiguate.");
            return new SymbolResolverResult([], 1);
        }

        return new SymbolResolverResult(candidates, 0);
    }
}
