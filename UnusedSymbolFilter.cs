using Microsoft.CodeAnalysis;

public static class UnusedSymbolFilter
{
    private const string MainMethodName = "Main";
    private const string TopLevelMainMethodName = "<Main>$";

    public static bool ShouldExclude(ISymbol symbol)
    {
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

        if (ImplementsInterfaceMember(symbol))
        {
            return true;
        }

        return false;
    }

    private static bool ImplementsInterfaceMember(ISymbol symbol)
    {
        INamedTypeSymbol? containingType = symbol.ContainingType;
        if (containingType is null)
        {
            return false;
        }

        foreach (INamedTypeSymbol iface in containingType.AllInterfaces)
        {
            foreach (ISymbol member in iface.GetMembers())
            {
                ISymbol? implementation = containingType.FindImplementationForInterfaceMember(member);
                if (SymbolEqualityComparer.Default.Equals(implementation, symbol))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
