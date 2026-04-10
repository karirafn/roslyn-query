using Microsoft.CodeAnalysis;

namespace RoslynQuery;

public static class MemberFormatter
{
    private static readonly SymbolDisplayFormat DeclaringTypeFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    public static IReadOnlyList<string> FormatMembers(INamedTypeSymbol type, bool inherited)
    {
        ArgumentNullException.ThrowIfNull(type);

        List<string> results = [];

        foreach (ISymbol member in type.GetMembers()
            .OrderBy(m => m.Kind)
            .ThenBy(m => m.Name))
        {
            string? formatted = FormatMember(member);
            if (formatted is not null)
            {
                results.Add(formatted);
            }
        }

        if (inherited)
        {
            INamedTypeSymbol? baseType = type.BaseType;
            while (baseType is not null)
            {
                string declaringType = baseType.ToDisplayString(DeclaringTypeFormat);
                foreach (ISymbol member in baseType.GetMembers()
                    .OrderBy(m => m.Kind)
                    .ThenBy(m => m.Name))
                {
                    string? formatted = FormatMember(member);
                    if (formatted is not null)
                    {
                        results.Add($"{formatted}\t{declaringType}");
                    }
                }
                baseType = baseType.BaseType;
            }
        }

        return results;
    }

    private static string? FormatMember(ISymbol member)
    {
        if (member.IsImplicitlyDeclared)
        {
            return null;
        }

        return member switch
        {
            IPropertySymbol p =>
                $"property\t{p.Type.ToDisplayString()} {p.Name}",
            IMethodSymbol m when m.MethodKind == MethodKind.Ordinary =>
                $"method\t{m.ReturnType.ToDisplayString()} {m.Name}({string.Join(", ", m.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"))})",
            IMethodSymbol m when m.MethodKind == MethodKind.Constructor =>
                $"constructor\t{m.ContainingType.Name}({string.Join(", ", m.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"))})",
            IFieldSymbol f =>
                $"field\t{f.Type.ToDisplayString()} {f.Name}",
            IEventSymbol e =>
                $"event\t{e.Type.ToDisplayString()} {e.Name}",
            _ => null
        };
    }
}
