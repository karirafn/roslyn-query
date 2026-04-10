using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynQuery;

public static class DeclarationFilter
{
    public static bool IsDeclarationSite(
        SyntaxTree? referenceTree,
        TextSpan referenceSpan,
        ImmutableArray<Location> declarationLocations)
    {
        if (referenceTree is null)
        {
            return false;
        }

        foreach (Location declaration in declarationLocations)
        {
            if (declaration.SourceTree == referenceTree && declaration.SourceSpan == referenceSpan)
            {
                return true;
            }
        }

        return false;
    }
}
