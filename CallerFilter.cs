using Microsoft.CodeAnalysis.FindSymbols;

public static class CallerFilter
{
    public static List<SymbolCallerInfo> GetDirectCallers(IEnumerable<SymbolCallerInfo> callers)
    {
        return callers
            .Where(c => c.IsDirect)
            .ToList();
    }
}
