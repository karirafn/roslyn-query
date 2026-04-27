using Microsoft.CodeAnalysis;

namespace RoslynQuery;

internal static class NamespaceWalker
{
    internal static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
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

    internal static IEnumerable<INamedTypeSymbol> GetNestedTypes(INamedTypeSymbol type)
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
