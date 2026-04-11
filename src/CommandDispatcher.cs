using System.Collections.Concurrent;
using System.Globalization;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynQuery;

public static class CommandDispatcher
{
    public static async Task<int> ExecuteAsync(string[] args, CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(context);

        if (args.Length == 0)
        {
            await PrintUsageAsync(context.Stderr);
            return 1;
        }

        bool showContext = args.Any(a => a is "--context");
        bool all = args.Any(a => a is "--all");
        bool inherited = args.Any(a => a is "--inherited");
        bool absolute = args.Any(a => a is "--absolute");
        bool compact = args.Any(a => a is "--compact");
        bool count = args.Any(a => a is "--count");

        int limit = 0;
        int limitIdx = Array.IndexOf(args, "--limit");
        if (limitIdx >= 0
            && limitIdx + 1 < args.Length
            && int.TryParse(args[limitIdx + 1], out int parsed))
        {
            limit = parsed;
        }

        HashSet<int> limitIndicesToSkip = [];
        if (limitIdx >= 0)
        {
            limitIndicesToSkip.Add(limitIdx);
            if (limitIdx + 1 < args.Length
                && int.TryParse(args[limitIdx + 1], out _))
            {
                limitIndicesToSkip.Add(limitIdx + 1);
            }
        }

        string[] filteredArgs = args
            .Where((a, i) => a is not (
                "--quiet" or "-q" or "--context" or "--all"
                or "--inherited" or "--absolute" or "--compact" or "--count")
                && !limitIndicesToSkip.Contains(i))
            .ToArray();

        if (filteredArgs.Length == 0)
        {
            await PrintUsageAsync(context.Stderr);
            return 1;
        }

        string command = filteredArgs[0];
        string[] rest = filteredArgs[1..];

        string? basePath = absolute ? null : context.SolutionDirectory;
        if (basePath is "")
        {
            basePath = null;
        }

        if (count && limit > 0)
        {
            await context.Stderr.WriteLineAsync(
                "--count and --limit are mutually exclusive");
            return 1;
        }

        if (count && command is "find-base" or "list-members" or "describe")
        {
            await context.Stderr.WriteLineAsync(
                $"--count is not supported on {command}");
            return 1;
        }

        LimitedWriter? limitedWriter = null;
        TextWriter originalStdout = context.Stdout;
        CommandContext effectiveContext = context;

        CountingWriter? countingWriter = null;

        if (count)
        {
            countingWriter = new CountingWriter();
            effectiveContext = new CommandContext(
                countingWriter,
                TextWriter.Null,
                context.Solution,
                context.SolutionDirectory);
        }
        else if (limit > 0)
        {
            limitedWriter = new LimitedWriter(originalStdout, limit);
            effectiveContext = new CommandContext(
                limitedWriter,
                context.Stderr,
                context.Solution,
                context.SolutionDirectory);
        }

        int result = command switch
        {
            "find-refs" => await FindRefs(rest, showContext, all, basePath, effectiveContext),
            "find-impl" => await FindImpl(rest, showContext, basePath, effectiveContext),
            "find-ctor" => await FindCtor(rest, showContext, basePath, effectiveContext),
            "find-overrides" => await FindOverrides(
                rest, showContext, all, basePath, compact, effectiveContext),
            "find-attribute" => await FindAttribute(
                rest, showContext, basePath, effectiveContext),
            "find-callers" => await FindCallers(
                rest, showContext, all, basePath, compact, effectiveContext),
            "find-base" => await FindBase(rest, basePath, effectiveContext),
            "find-unused" => await FindUnused(showContext, basePath, effectiveContext),
            "list-members" => await ListMembers(rest, inherited, all, effectiveContext),
            "list-types" => await ListTypes(rest, showContext, basePath, effectiveContext),
            "describe" => await Describe(rest, basePath, effectiveContext),
            _ => await FailAsync($"Unknown command: {command}", effectiveContext.Stderr),
        };

        if (countingWriter is not null)
        {
            await originalStdout.WriteLineAsync(
                countingWriter.Count.ToString(CultureInfo.InvariantCulture));
        }

        if (limitedWriter is { Suppressed: > 0 })
        {
            await context.Stderr.WriteLineAsync(
                $"... ({limitedWriter.Suppressed} more, omit --limit to see all)");
        }

        return result;
    }

    internal static async Task PrintUsageAsync(TextWriter stderr)
    {
        await stderr.WriteLineAsync("Usage: roslyn-query <command> <symbol> [solution.sln|.slnx] [flags]");
        await stderr.WriteLineAsync();
        await stderr.WriteLineAsync("Commands:");
        await stderr.WriteLineAsync(
            "  find-refs <Symbol>         All references to a type, property, or method");
        await stderr.WriteLineAsync(
            "  find-callers <Symbol>      All invocation call sites of a method");
        await stderr.WriteLineAsync(
            "  find-impl <Type>           All implementations/subclasses of an interface or class");
        await stderr.WriteLineAsync(
            "  find-ctor <Type>           All constructor call sites (new X(...))");
        await stderr.WriteLineAsync(
            "  find-overrides <Member>    All overrides of a virtual/abstract member");
        await stderr.WriteLineAsync(
            "  find-attribute <Attr>      All symbols decorated with an attribute");
        await stderr.WriteLineAsync(
            "  find-base <Type>           Inheritance chain and implemented interfaces");
        await stderr.WriteLineAsync(
            "  find-unused                All symbols with zero references");
        await stderr.WriteLineAsync(
            "  list-members <Type>        All members of a type (properties, methods, fields)");
        await stderr.WriteLineAsync(
            "  list-types <Namespace>     All types in a namespace (prefix match)");
        await stderr.WriteLineAsync(
            "  batch                      Read commands from stdin, one per line (uses daemon)");
        await stderr.WriteLineAsync(
            "  daemon stop [solution.sln|.slnx] Stop the background daemon for a solution");
        await stderr.WriteLineAsync();
        await stderr.WriteLineAsync("Flags:");
        await stderr.WriteLineAsync(
            "  --quiet, -q                Suppress workspace loading warnings");
        await stderr.WriteLineAsync(
            "  --context                  Show trimmed source line alongside file:line output");
        await stderr.WriteLineAsync(
            "  --all                      Return results for all matching symbols when ambiguous");
        await stderr.WriteLineAsync(
            "  --inherited                Include inherited members in list-members output");
        await stderr.WriteLineAsync(
            "  --absolute                 Show absolute file paths (default: relative to solution)");
        await stderr.WriteLineAsync(
            "  --limit N                  Cap output to N lines (remainder count on stderr)");
        await stderr.WriteLineAsync(
            "  --compact                  Short symbol names in find-callers/find-overrides");
        await stderr.WriteLineAsync(
            "  --count                    Print only the result count (not supported on find-base or list-members)");
        await stderr.WriteLineAsync();
        await stderr.WriteLineAsync("Internal:");
        await stderr.WriteLineAsync(
            "  --daemon <solution.sln|.slnx> Run as daemon server for the given solution");
        await stderr.WriteLineAsync();
        await stderr.WriteLineAsync(
            "If solution path is omitted, searches parent directories for a .sln or .slnx file.");
        await stderr.WriteLineAsync("Symbol format: TypeName  or  TypeName.MemberName");
        await stderr.WriteLineAsync();
        await stderr.WriteLineAsync(
            "Repeated queries automatically use a background daemon for fast responses");
        await stderr.WriteLineAsync(
            "(under 1 second after the first query).");
    }

    private static string FormatLocation(
        FileLinePositionSpan span,
        bool context,
        SyntaxTree? tree,
        string? basePath = null)
        => LocationFormatter.Format(span, context, tree, basePath);

    private static string FormatTypeKind(TypeKind kind) => kind switch
    {
        TypeKind.Class => "class",
        TypeKind.Interface => "interface",
        TypeKind.Struct => "struct",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        TypeKind.Module => "module",
        _ => kind.ToString(),
    };

    public static string FormatSymbolName(ISymbol symbol, bool compact)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        if (!compact)
        {
            return symbol.ToDisplayString();
        }

        string memberName = symbol.Name;
        string? typeName = symbol.ContainingType?.Name;
        return typeName is not null
            ? $"{typeName}.{memberName}"
            : memberName;
    }

    private static async Task<int> FailAsync(string message, TextWriter stderr)
    {
        await stderr.WriteLineAsync($"error: {message}");
        return 1;
    }

    internal static async Task<List<ISymbol>> FindSymbolsByName(Solution solution, string symbolName)
    {
        string[] parts = symbolName.Split('.', 2);
        string memberName = parts[^1];
        string? typeName = parts.Length > 1 ? parts[0] : null;

        List<ISymbol> found = [];
        HashSet<string> seen = new();

        foreach (Project project in solution.Projects)
        {
            Compilation? compilation = await project.GetCompilationAsync();
            if (compilation is null)
            {
                continue;
            }

            IEnumerable<ISymbol> candidates = compilation.GetSymbolsWithName(
                name => name == memberName,
                SymbolFilter.All);

            foreach (ISymbol symbol in candidates)
            {
                if (typeName is not null && symbol.ContainingType?.Name != typeName)
                {
                    continue;
                }

                string key = symbol.ToDisplayString();
                if (seen.Add(key))
                {
                    found.Add(symbol);
                }
            }
        }

        return found;
    }

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

    internal static async Task<INamedTypeSymbol?> FindTypeByName(
        Solution solution,
        string typeName)
    {
        foreach (Project project in solution.Projects)
        {
            Compilation? compilation = await project.GetCompilationAsync();
            if (compilation is null)
            {
                continue;
            }

            INamedTypeSymbol? target = compilation
                .GetSymbolsWithName(typeName, SymbolFilter.Type)
                .OfType<INamedTypeSymbol>()
                .FirstOrDefault(t => t.Locations.Any(l => l.IsInSource));

            if (target is not null)
            {
                return target;
            }
        }
        return null;
    }

    // -- find-refs ----------------------------------------------------------------

    private static async Task<int> FindRefs(
        string[] args,
        bool context,
        bool all,
        string? basePath,
        CommandContext ctx)
    {
        if (args.Length == 0)
        {
            return await FailAsync("find-refs requires a symbol name", ctx.Stderr);
        }

        string symbolName = args[0];
        Solution solution = ctx.Solution;

        List<ISymbol> candidates = await FindSymbolsByName(solution, symbolName);
        SymbolResolverResult resolved = SymbolResolver.ResolveOrAll(
            candidates,
            symbolName,
            all,
            ctx.Stderr);
        if (resolved.ExitCode != 0)
        {
            return resolved.ExitCode;
        }
        if (resolved.Symbols.Count == 0)
        {
            return 0;
        }

        bool multipleSymbols = resolved.Symbols.Count > 1;
        int totalCount = 0;

        foreach (ISymbol symbol in resolved.Symbols)
        {
            if (multipleSymbols)
            {
                await ctx.Stdout.WriteLineAsync($"# {symbol.ToDisplayString()}");
            }

            IEnumerable<ReferencedSymbol> refs =
                await SymbolFinder.FindReferencesAsync(symbol, solution);
            IEnumerable<Location> locations = refs
                .SelectMany(r => r.Locations)
                .Select(l => l.Location);
            foreach (Location loc in locations)
            {
                if (DeclarationFilter.IsDeclarationSite(
                    loc.SourceTree,
                    loc.SourceSpan,
                    symbol.Locations))
                {
                    continue;
                }

                FileLinePositionSpan span = loc.GetLineSpan();
                await ctx.Stdout.WriteLineAsync(
                    FormatLocation(span, context, loc.SourceTree, basePath));
                totalCount++;
            }
        }

        if (totalCount == 0 && !multipleSymbols)
        {
            await ctx.Stderr.WriteLineAsync("No references found.");
        }

        return 0;
    }

    // -- find-impl ----------------------------------------------------------------

    private static async Task<int> FindImpl(
        string[] args,
        bool context,
        string? basePath,
        CommandContext ctx)
    {
        if (args.Length == 0)
        {
            return await FailAsync("find-impl requires a type name", ctx.Stderr);
        }

        string typeName = args[0];
        Solution solution = ctx.Solution;

        INamedTypeSymbol? target = await FindTypeByName(solution, typeName);
        if (target is null)
        {
            return await FailAsync($"Type not found: {typeName}", ctx.Stderr);
        }

        IEnumerable<INamedTypeSymbol> results = target.TypeKind == TypeKind.Interface
            ? await SymbolFinder.FindImplementationsAsync(target, solution)
            : await SymbolFinder.FindDerivedClassesAsync(target, solution);

        foreach (INamedTypeSymbol impl in results)
        {
            Location? loc = impl.Locations.FirstOrDefault(l => l.IsInSource);
            if (loc is null)
            {
                continue;
            }
            FileLinePositionSpan span = loc.GetLineSpan();
            string location = FormatLocation(span, context, loc.SourceTree, basePath);
            await ctx.Stdout.WriteLineAsync($"{location}\t{impl.ToDisplayString()}");
        }

        return 0;
    }

    // -- find-ctor ----------------------------------------------------------------

    private static async Task<int> FindCtor(
        string[] args,
        bool context,
        string? basePath,
        CommandContext ctx)
    {
        if (args.Length == 0)
        {
            return await FailAsync("find-ctor requires a type name", ctx.Stderr);
        }

        string typeName = args[0];
        Solution solution = ctx.Solution;

        INamedTypeSymbol? target = await FindTypeByName(solution, typeName);
        if (target is null)
        {
            return await FailAsync($"Type not found: {typeName}", ctx.Stderr);
        }

        int count = 0;
        HashSet<string> seen = new();

        foreach (IMethodSymbol ctor in target.Constructors)
        {
            IEnumerable<ReferencedSymbol> refs =
                await SymbolFinder.FindReferencesAsync(ctor, solution);
            IEnumerable<Location> allLocations = refs
                .SelectMany(r => r.Locations)
                .Select(l => l.Location);
            foreach (Location loc in allLocations)
            {
                FileLinePositionSpan span = loc.GetLineSpan();
                string key = $"{span.Path}:{span.StartLinePosition.Line + 1}";
                if (seen.Add(key))
                {
                    await ctx.Stdout.WriteLineAsync(
                        FormatLocation(span, context, loc.SourceTree, basePath));
                    count++;
                }
            }
        }

        if (count == 0)
        {
            await ctx.Stderr.WriteLineAsync("No constructor call sites found.");
        }

        return 0;
    }

    // -- find-overrides -----------------------------------------------------------

    private static async Task<int> FindOverrides(
        string[] args,
        bool context,
        bool all,
        string? basePath,
        bool compact,
        CommandContext ctx)
    {
        if (args.Length == 0)
        {
            return await FailAsync("find-overrides requires a member name", ctx.Stderr);
        }

        string symbolName = args[0];
        Solution solution = ctx.Solution;

        List<ISymbol> candidates = await FindSymbolsByName(solution, symbolName);
        List<ISymbol> overridable = candidates
            .Where(s => s is IMethodSymbol m && (m.IsVirtual || m.IsAbstract || m.IsOverride)
                     || s is IPropertySymbol p && (p.IsVirtual || p.IsAbstract || p.IsOverride))
            .ToList();

        if (overridable.Count == 0 && candidates.Count > 0)
        {
            return await FailAsync(
                $"'{symbolName}' is not virtual or abstract",
                ctx.Stderr);
        }

        SymbolResolverResult resolved = SymbolResolver.ResolveOrAll(
            overridable,
            symbolName,
            all,
            ctx.Stderr);
        if (resolved.ExitCode != 0)
        {
            return resolved.ExitCode;
        }
        if (resolved.Symbols.Count == 0)
        {
            return 0;
        }

        bool multipleSymbols = resolved.Symbols.Count > 1;

        foreach (ISymbol symbol in resolved.Symbols)
        {
            if (multipleSymbols)
            {
                await ctx.Stdout.WriteLineAsync($"# {symbol.ToDisplayString()}");
            }

            IEnumerable<ISymbol> overrides =
                await SymbolFinder.FindOverridesAsync(symbol, solution);
            foreach (ISymbol o in overrides)
            {
                Location? loc = o.Locations.FirstOrDefault(l => l.IsInSource);
                if (loc is null)
                {
                    continue;
                }
                FileLinePositionSpan span = loc.GetLineSpan();
                string location = FormatLocation(span, context, loc.SourceTree, basePath);
                await ctx.Stdout.WriteLineAsync(
                    $"{location}\t{FormatSymbolName(o, compact)}");
            }
        }

        return 0;
    }

    // -- find-attribute -----------------------------------------------------------

    private static async Task<int> FindAttribute(
        string[] args,
        bool context,
        string? basePath,
        CommandContext ctx)
    {
        if (args.Length == 0)
        {
            return await FailAsync("find-attribute requires an attribute name", ctx.Stderr);
        }

        string attrName = args[0].Trim('[', ']');
        Solution solution = ctx.Solution;

        Compilation?[] compilations = await Task.WhenAll(
            solution.Projects.Select(p => p.GetCompilationAsync()));

        ConcurrentBag<AttributeMatch> bag = [];
        Parallel.ForEach(
            compilations.Where(c => c is not null).Cast<Compilation>(),
            compilation =>
            {
                IReadOnlyList<AttributeMatch> matches =
                    AttributeScanner.ScanCompilation(compilation, attrName);
                foreach (AttributeMatch match in matches)
                {
                    bag.Add(match);
                }
            });

        IReadOnlyList<AttributeMatch> results = AttributeScanner.DeduplicateAndSort([.. bag]);

        foreach (AttributeMatch result in results)
        {
            string location = FormatLocation(result.Span, context, result.Tree, basePath);
            await ctx.Stdout.WriteLineAsync($"{location}\t{result.FullyQualifiedName}");
        }

        if (results.Count == 0)
        {
            await ctx.Stderr.WriteLineAsync(
                $"No symbols found with attribute '{attrName}'.");
        }

        return 0;
    }

    // -- find-callers -------------------------------------------------------------

    private static async Task<int> FindCallers(
        string[] args,
        bool context,
        bool all,
        string? basePath,
        bool compact,
        CommandContext ctx)
    {
        if (args.Length == 0)
        {
            return await FailAsync("find-callers requires a symbol name", ctx.Stderr);
        }

        string symbolName = args[0];
        Solution solution = ctx.Solution;

        List<ISymbol> candidates = await FindSymbolsByName(solution, symbolName);
        SymbolResolverResult resolved = SymbolResolver.ResolveOrAll(
            candidates,
            symbolName,
            all,
            ctx.Stderr);
        if (resolved.ExitCode != 0)
        {
            return resolved.ExitCode;
        }
        if (resolved.Symbols.Count == 0)
        {
            return 0;
        }

        bool multipleSymbols = resolved.Symbols.Count > 1;
        int totalCount = 0;

        foreach (ISymbol symbol in resolved.Symbols)
        {
            if (multipleSymbols)
            {
                await ctx.Stdout.WriteLineAsync($"# {symbol.ToDisplayString()}");
            }

            IEnumerable<SymbolCallerInfo> callers =
                await SymbolFinder.FindCallersAsync(symbol, solution);
            IReadOnlyList<SymbolCallerInfo> directCallers =
                CallerFilter.GetDirectCallers(callers);

            foreach (SymbolCallerInfo caller in directCallers)
            {
                foreach (Location location in caller.Locations)
                {
                    FileLinePositionSpan span = location.GetLineSpan();
                    string formatted = FormatLocation(
                        span,
                        context,
                        location.SourceTree,
                        basePath);
                    await ctx.Stdout.WriteLineAsync(
                        $"{formatted}\t{FormatSymbolName(caller.CallingSymbol, compact)}");
                    totalCount++;
                }
            }
        }

        if (totalCount == 0 && !multipleSymbols)
        {
            await ctx.Stderr.WriteLineAsync("No callers found.");
        }

        return 0;
    }

    // -- find-unused --------------------------------------------------------------

    private static async Task<int> FindUnused(
        bool context,
        string? basePath,
        CommandContext ctx)
    {
        Solution solution = ctx.Solution;

        int count = 0;
        HashSet<string> seen = new();

        foreach (Project project in solution.Projects)
        {
            Compilation? compilation = await project.GetCompilationAsync();
            if (compilation is null)
            {
                continue;
            }

            foreach (INamedTypeSymbol type in GetAllTypes(compilation.GlobalNamespace))
            {
                if (!type.Locations.Any(l => l.IsInSource))
                {
                    continue;
                }

                HashSet<ISymbol> interfaceImplementingSymbols =
                    UnusedSymbolFilter.GetInterfaceImplementingSymbols(type);

                List<ISymbol> symbols = [type];
                symbols.AddRange(type.GetMembers());

                foreach (ISymbol symbol in symbols)
                {
                    if (UnusedSymbolFilter.ShouldExclude(
                        symbol,
                        interfaceImplementingSymbols))
                    {
                        continue;
                    }

                    string key = symbol.ToDisplayString();
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    IEnumerable<ReferencedSymbol> refs =
                        await SymbolFinder.FindReferencesAsync(symbol, solution);
                    bool hasNonDeclarationReference = refs
                        .SelectMany(r => r.Locations)
                        .Select(l => l.Location)
                        .Any(loc => !DeclarationFilter.IsDeclarationSite(
                            loc.SourceTree,
                            loc.SourceSpan,
                            symbol.Locations));

                    if (!hasNonDeclarationReference)
                    {
                        Location? loc = symbol.Locations
                            .FirstOrDefault(l => l.IsInSource);
                        if (loc is not null)
                        {
                            FileLinePositionSpan span = loc.GetLineSpan();
                            string location = FormatLocation(
                                span,
                                context,
                                loc.SourceTree,
                                basePath);
                            await ctx.Stdout.WriteLineAsync(
                                $"{location}\t{symbol.ToDisplayString()}");
                            count++;
                        }
                    }
                }
            }
        }

        if (count == 0)
        {
            await ctx.Stderr.WriteLineAsync("No unused symbols found.");
        }

        return 0;
    }

    // -- find-base ----------------------------------------------------------------

    private static async Task<int> FindBase(string[] args, string? basePath, CommandContext ctx)
    {
        if (args.Length == 0)
        {
            return await FailAsync("find-base requires a type name", ctx.Stderr);
        }

        string typeName = args[0];
        Solution solution = ctx.Solution;

        INamedTypeSymbol? target = await FindTypeByName(solution, typeName);
        if (target is null)
        {
            return await FailAsync($"Type not found: {typeName}", ctx.Stderr);
        }

        INamedTypeSymbol? baseType = target.BaseType;
        while (baseType is not null
            && baseType.SpecialType != SpecialType.System_Object)
        {
            Location? loc = baseType.Locations.FirstOrDefault(l => l.IsInSource);
            string src = loc is not null
                ? FormatLocation(loc.GetLineSpan(), context: false, loc.SourceTree, basePath)
                : "(external)";
            await ctx.Stdout.WriteLineAsync(
                $"base\t{baseType.ToDisplayString()}\t{src}");
            baseType = baseType.BaseType;
        }

        foreach (INamedTypeSymbol iface in target.AllInterfaces)
        {
            Location? loc = iface.Locations.FirstOrDefault(l => l.IsInSource);
            string src = loc is not null
                ? FormatLocation(loc.GetLineSpan(), context: false, loc.SourceTree, basePath)
                : "(external)";
            await ctx.Stdout.WriteLineAsync(
                $"interface\t{iface.ToDisplayString()}\t{src}");
        }

        return 0;
    }

    // -- list-members -------------------------------------------------------------

    private static async Task<int> ListMembers(
        string[] args,
        bool inherited,
        bool all,
        CommandContext ctx)
    {
        if (args.Length == 0)
        {
            return await FailAsync("list-members requires a type name", ctx.Stderr);
        }

        string typeName = args[0];
        Solution solution = ctx.Solution;

        INamedTypeSymbol? target = await FindTypeByName(solution, typeName);
        List<INamedTypeSymbol> targets;

        if (target is not null)
        {
            targets = [target];
        }
        else
        {
            List<Compilation> compilations = [];
            foreach (Project project in solution.Projects)
            {
                Compilation? compilation = await project.GetCompilationAsync();
                if (compilation is not null)
                {
                    compilations.Add(compilation);
                }
            }

            targets = MetadataTypeResolver
                .FindMetadataTypes(compilations, typeName)
                .ToList();

            if (targets.Count == 0)
            {
                return await FailAsync($"Type not found: {typeName}", ctx.Stderr);
            }

            if (targets.Count > 1 && !all)
            {
                await ctx.Stderr.WriteLineAsync(
                    $"Ambiguous '{typeName}' — {targets.Count} matches:");
                foreach (INamedTypeSymbol t in targets)
                {
                    await ctx.Stderr.WriteLineAsync($"  {t.ToDisplayString()}");
                }
                await ctx.Stderr.WriteLineAsync(
                    "Use a fully-qualified name to disambiguate, or pass --all.");
                return 1;
            }
        }

        bool multipleTypes = targets.Count > 1;

        foreach (INamedTypeSymbol t in targets)
        {
            if (multipleTypes)
            {
                await ctx.Stdout.WriteLineAsync($"# {t.ToDisplayString()}");
            }

            IReadOnlyList<string> lines = MemberFormatter.FormatMembers(t, inherited);
            foreach (string line in lines)
            {
                await ctx.Stdout.WriteLineAsync(line);
            }
        }

        return 0;
    }

    // -- describe -----------------------------------------------------------------

    private static async Task<int> Describe(string[] args, string? basePath, CommandContext ctx)
    {
        if (args.Length == 0)
        {
            return await FailAsync("describe requires a type name", ctx.Stderr);
        }

        _ = basePath;
        return 0;
    }

    // -- list-types ---------------------------------------------------------------

    private static async Task<int> ListTypes(
        string[] args,
        bool context,
        string? basePath,
        CommandContext ctx)
    {
        if (args.Length == 0)
        {
            return await FailAsync("list-types requires a namespace", ctx.Stderr);
        }

        string namespaceName = args[0];
        Solution solution = ctx.Solution;

        HashSet<string> seen = new();
        int count = 0;

        foreach (Project project in solution.Projects)
        {
            Compilation? compilation = await project.GetCompilationAsync();
            if (compilation is null)
            {
                continue;
            }

            foreach (INamedTypeSymbol type in GetAllTypes(compilation.GlobalNamespace))
            {
                if (!type.ContainingNamespace.ToDisplayString()
                    .StartsWith(namespaceName, StringComparison.Ordinal))
                {
                    continue;
                }

                Location? loc = type.Locations.FirstOrDefault(l => l.IsInSource);
                if (loc is null)
                {
                    continue;
                }

                string key = type.ToDisplayString();
                if (!seen.Add(key))
                {
                    continue;
                }

                FileLinePositionSpan span = loc.GetLineSpan();
                string location = FormatLocation(span, context, loc.SourceTree, basePath);
                string typeKind = FormatTypeKind(type.TypeKind);
                await ctx.Stdout.WriteLineAsync(
                    $"{typeKind}\t{type.ToDisplayString()}\t{location}");
                count++;
            }
        }

        if (count == 0)
        {
            await ctx.Stderr.WriteLineAsync(
                $"No types found in namespace '{namespaceName}'.");
        }

        return 0;
    }
}
