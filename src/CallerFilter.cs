using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynQuery;

public static class CallerFilter
{
    public static IReadOnlyList<SymbolCallerInfo> GetDirectCallers(IEnumerable<SymbolCallerInfo> callers)
    {
        ArgumentNullException.ThrowIfNull(callers);

        return callers
            .Where(c => c.IsDirect)
            .ToList();
    }
}
