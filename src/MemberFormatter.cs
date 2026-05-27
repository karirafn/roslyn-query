using Microsoft.CodeAnalysis;

namespace RoslynQuery;

public static class MemberFormatter
{
    private static readonly SymbolDisplayFormat DeclaringTypeFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    public static IReadOnlyList<string> FormatMembers(
        INamedTypeSymbol type,
        bool inherited,
        TextWriter? stderr = null)
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
                EmitUnresolvedTypeWarnings(member, stderr);
                results.Add(formatted);
            }
        }

        if (inherited)
        {
            if (type.TypeKind == TypeKind.Interface)
            {
                HashSet<string> seen = new(results.Select(r => r.Split('\t')[1]));
                foreach (INamedTypeSymbol parentInterface in type.AllInterfaces)
                {
                    string declaringType = parentInterface.ToDisplayString(DeclaringTypeFormat);
                    foreach (ISymbol member in parentInterface.GetMembers()
                        .OrderBy(m => m.Kind)
                        .ThenBy(m => m.Name))
                    {
                        string? formatted = FormatMember(member);
                        if (formatted is not null && seen.Add(formatted))
                        {
                            EmitUnresolvedTypeWarnings(member, stderr);
                            results.Add($"{formatted}\t{declaringType}");
                        }
                    }
                }
            }
            else
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
                            EmitUnresolvedTypeWarnings(member, stderr);
                            results.Add($"{formatted}\t{declaringType}");
                        }
                    }
                    baseType = baseType.BaseType;
                }
            }
        }

        return results;
    }

    private static void EmitUnresolvedTypeWarnings(ISymbol member, TextWriter? stderr)
    {
        if (stderr is null)
        {
            return;
        }

        bool hasErrorType = member switch
        {
            IMethodSymbol m => m.ReturnType.TypeKind == TypeKind.Error
                || m.Parameters.Any(p => p.Type.TypeKind == TypeKind.Error),
            IPropertySymbol p => p.Type.TypeKind == TypeKind.Error,
            IFieldSymbol f => f.Type.TypeKind == TypeKind.Error,
            IEventSymbol e => e.Type.TypeKind == TypeKind.Error,
            _ => false,
        };

        if (hasErrorType)
        {
            stderr.WriteLine(
                $"warning: unresolved types in {member.ContainingType.Name}.{member.Name}");
        }
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
            IMethodSymbol m when m.MethodKind == MethodKind.ExplicitInterfaceImplementation =>
                $"method\t{m.ReturnType.ToDisplayString()} {m.ExplicitInterfaceImplementations[0].ContainingType.Name}.{m.ExplicitInterfaceImplementations[0].Name}({string.Join(", ", m.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}"))})",
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
