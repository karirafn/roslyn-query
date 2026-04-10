using Microsoft.CodeAnalysis;

public static class MetadataTypeResolver
{
    public static List<INamedTypeSymbol> FindMetadataTypes(
        IEnumerable<Compilation> compilations,
        string typeName)
    {
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
                    typeName))
                {
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

    private static IEnumerable<INamedTypeSymbol> FindTypesInNamespace(
        INamespaceSymbol ns,
        string typeName)
    {
        foreach (INamedTypeSymbol type in ns.GetTypeMembers(typeName))
        {
            yield return type;
        }

        foreach (INamespaceSymbol childNs in ns.GetNamespaceMembers())
        {
            foreach (INamedTypeSymbol type in FindTypesInNamespace(childNs, typeName))
            {
                yield return type;
            }
        }
    }
}
