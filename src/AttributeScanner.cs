using Microsoft.CodeAnalysis;

namespace RoslynQuery;

public sealed record AttributeMatch(
    string Path,
    int Line,
    string FullyQualifiedName,
    FileLinePositionSpan Span,
    SyntaxTree? Tree);

public static class AttributeScanner
{
    private const string AttributeSuffix = "Attribute";

    public static IReadOnlyList<AttributeMatch> ScanCompilation(Compilation compilation, string attrName)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(attrName);

        List<AttributeMatch> results = [];

        foreach (INamedTypeSymbol type in GetAllTypes(compilation.GlobalNamespace))
        {
            TryAddMatch(type, attrName, results);
            foreach (ISymbol member in type.GetMembers())
            {
                TryAddMatch(member, attrName, results);
            }
        }

        return results;
    }

    public static IReadOnlyList<AttributeMatch> DeduplicateAndSort(IEnumerable<AttributeMatch> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        HashSet<string> seen = new();
        return results
            .Where(r => seen.Add($"{r.Path}:{r.Line}"))
            .OrderBy(r => r.Path, StringComparer.Ordinal)
            .ThenBy(r => r.Line)
            .ToList();
    }

    private static void TryAddMatch(
        ISymbol symbol,
        string attrName,
        List<AttributeMatch> results)
    {
        bool match = symbol.GetAttributes().Any(a =>
        {
            string? name = a.AttributeClass?.Name;
            if (name is null)
            {
                return false;
            }
            string bare = name.EndsWith(AttributeSuffix, StringComparison.Ordinal)
                ? name[..^AttributeSuffix.Length]
                : name;
            return name == attrName || bare == attrName;
        });

        if (!match)
        {
            return;
        }

        Location? loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc is null)
        {
            return;
        }

        FileLinePositionSpan span = loc.GetLineSpan();
        results.Add(new AttributeMatch(
            span.Path,
            span.StartLinePosition.Line + 1,
            symbol.ToDisplayString(),
            span,
            loc.SourceTree));
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
    {
        foreach (INamedTypeSymbol type in ns.GetTypeMembers())
        {
            yield return type;
            foreach (INamedTypeSymbol nested in GetNestedTypes(type))
            {
                yield return nested;
            }
        }
        foreach (INamespaceSymbol childNs in ns.GetNamespaceMembers())
        {
            foreach (INamedTypeSymbol type in GetAllTypes(childNs))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetNestedTypes(INamedTypeSymbol type)
    {
        foreach (INamedTypeSymbol nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (INamedTypeSymbol n in GetNestedTypes(nested))
            {
                yield return n;
            }
        }
    }
}
