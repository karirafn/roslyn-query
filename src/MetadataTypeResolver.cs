using Microsoft.CodeAnalysis;

namespace RoslynQuery;

public static class MetadataTypeResolver
{
    public static IReadOnlyList<ISymbol> FindMetadataMembers(
        IEnumerable<Compilation> compilations,
        string memberName,
        string? qualifier)
    {
        ArgumentNullException.ThrowIfNull(compilations);
        ArgumentNullException.ThrowIfNull(memberName);

        HashSet<string> seen = new();
        List<ISymbol> results = [];

        string? dottedQualifier = qualifier is not null ? $".{qualifier}" : null;

        foreach (Compilation compilation in compilations)
        {
            foreach (MetadataReference reference in compilation.References)
            {
                ISymbol? assemblySymbol = compilation.GetAssemblyOrModuleSymbol(reference);
                if (assemblySymbol is not IAssemblySymbol assembly)
                {
                    continue;
                }

                foreach (ISymbol member in FindMembersInNamespace(
                    assembly.GlobalNamespace,
                    memberName,
                    qualifier,
                    dottedQualifier))
                {
                    string key = member.ToDisplayString();
                    if (seen.Add(key))
                    {
                        results.Add(member);
                    }
                }
            }
        }

        return results;
    }

    private static IEnumerable<ISymbol> FindMembersInNamespace(
        INamespaceSymbol ns,
        string memberName,
        string? qualifier,
        string? dottedQualifier)
    {
        foreach (INamedTypeSymbol type in ns.GetTypeMembers())
        {
            foreach (ISymbol member in FindMembersInType(type, memberName, qualifier, dottedQualifier))
            {
                yield return member;
            }

            if (qualifier is null || TypeMatchesQualifier(type, qualifier, dottedQualifier!))
            {
                foreach (INamedTypeSymbol nested in GetNestedTypesRecursive(type))
                {
                    foreach (ISymbol member in FindMembersInType(nested, memberName, qualifier, dottedQualifier))
                    {
                        yield return member;
                    }
                }
            }
        }

        foreach (INamespaceSymbol childNs in ns.GetNamespaceMembers())
        {
            foreach (ISymbol member in FindMembersInNamespace(childNs, memberName, qualifier, dottedQualifier))
            {
                yield return member;
            }
        }
    }

    private static IEnumerable<ISymbol> FindMembersInType(
        INamedTypeSymbol type,
        string memberName,
        string? qualifier,
        string? dottedQualifier)
    {
        if (qualifier is not null && !TypeMatchesQualifier(type, qualifier, dottedQualifier!))
        {
            yield break;
        }

        foreach (ISymbol member in type.GetMembers(memberName))
        {
            yield return member;
        }
    }

    private static bool TypeMatchesQualifier(INamedTypeSymbol type, string qualifier, string dottedQualifier)
    {
        string displayFqn = type.ToDisplayString();
        if (displayFqn == qualifier
            || displayFqn.EndsWith(dottedQualifier, StringComparison.Ordinal))
        {
            return true;
        }

        // Also check the metadata-qualified name (e.g. "System.String" for the 'string' keyword alias)
        string ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        string metadataFqn = string.IsNullOrEmpty(ns)
            ? type.MetadataName
            : $"{ns}.{type.MetadataName}";
        return metadataFqn == qualifier
            || metadataFqn.EndsWith(dottedQualifier, StringComparison.Ordinal);
    }

    private static IEnumerable<INamedTypeSymbol> GetNestedTypesRecursive(INamedTypeSymbol type)
    {
        foreach (INamedTypeSymbol nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (INamedTypeSymbol n in GetNestedTypesRecursive(nested))
            {
                yield return n;
            }
        }
    }

    public static IReadOnlyList<INamedTypeSymbol> FindMetadataTypes(
        IEnumerable<Compilation> compilations,
        string typeName)
    {
        ArgumentNullException.ThrowIfNull(compilations);
        ArgumentNullException.ThrowIfNull(typeName);

        int lastDot = typeName.LastIndexOf('.');
        string simpleName = lastDot >= 0 ? typeName[(lastDot + 1)..] : typeName;
        string? namespaceQualifier = lastDot >= 0 ? typeName[..lastDot] : null;

        HashSet<string> seen = new();
        List<INamedTypeSymbol> results = [];

        foreach (Compilation compilation in compilations)
        {
            foreach (MetadataReference reference in compilation.References)
            {
                ISymbol? assemblySymbol = compilation.GetAssemblyOrModuleSymbol(reference);
                if (assemblySymbol is not IAssemblySymbol assembly)
                {
                    continue;
                }

                foreach (INamedTypeSymbol type in FindTypesInNamespace(
                    assembly.GlobalNamespace,
                    simpleName))
                {
                    if (namespaceQualifier is not null
                        && !TypeMatchesNamespaceQualifier(type, namespaceQualifier))
                    {
                        continue;
                    }

                    string key = type.ToDisplayString();
                    if (seen.Add(key))
                    {
                        results.Add(type);
                    }
                }
            }
        }

        return results;
    }

    private static bool TypeMatchesNamespaceQualifier(INamedTypeSymbol type, string qualifier)
    {
        string ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        string containingType = type.ContainingType?.ToDisplayString() ?? string.Empty;
        string container = string.IsNullOrEmpty(containingType) ? ns : containingType;

        return container == qualifier
            || container.EndsWith($".{qualifier}", StringComparison.Ordinal);
    }

    private static IEnumerable<INamedTypeSymbol> FindTypesInNamespace(
        INamespaceSymbol ns,
        string simpleName)
    {
        foreach (INamedTypeSymbol type in ns.GetTypeMembers(simpleName))
        {
            yield return type;
        }

        foreach (INamespaceSymbol childNs in ns.GetNamespaceMembers())
        {
            foreach (INamedTypeSymbol type in FindTypesInNamespace(childNs, simpleName))
            {
                yield return type;
            }
        }
    }
}
