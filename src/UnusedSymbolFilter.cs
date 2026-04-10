using Microsoft.CodeAnalysis;

namespace RoslynQuery;

public static class UnusedSymbolFilter
{
    private const string MainMethodName = "Main";
    private const string TopLevelMainMethodName = "<Main>$";

    public static HashSet<ISymbol> GetInterfaceImplementingSymbols(INamedTypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);

        HashSet<ISymbol> result = new(SymbolEqualityComparer.Default);

        foreach (INamedTypeSymbol iface in type.AllInterfaces)
        {
            foreach (ISymbol member in iface.GetMembers())
            {
                ISymbol? implementation = type.FindImplementationForInterfaceMember(member);
                if (implementation is not null)
                {
                    result.Add(implementation);
                }
            }
        }

        return result;
    }

    public static bool ShouldExclude(ISymbol symbol, HashSet<ISymbol> interfaceImplementingSymbols)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(interfaceImplementingSymbols);

        if (symbol.IsImplicitlyDeclared)
        {
            return true;
        }

        if (symbol is IMethodSymbol method)
        {
            if (method.Name is MainMethodName or TopLevelMainMethodName)
            {
                return true;
            }

            if (method.MethodKind == MethodKind.Constructor && method.Parameters.Length > 0)
            {
                return true;
            }
        }

        if (interfaceImplementingSymbols.Contains(symbol))
        {
            return true;
        }

        return false;
    }

    public static bool ShouldExclude(ISymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        INamedTypeSymbol? containingType = symbol.ContainingType;
        HashSet<ISymbol> interfaceSymbols = containingType is not null
            ? GetInterfaceImplementingSymbols(containingType)
            : new(SymbolEqualityComparer.Default);

        return ShouldExclude(symbol, interfaceSymbols);
    }
}
