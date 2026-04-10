using System.Collections.Concurrent;

using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;

using RoslynQuery;

MSBuildLocator.RegisterDefaults();
return await Run(args);

static async Task<int> Run(string[] args)
{
    if (args.Length == 0)
    {
        await PrintUsageAsync();
        return 1;
    }

    bool quiet = args.Any(a => a is "--quiet" or "-q");
    bool context = args.Any(a => a is "--context");
    bool all = args.Any(a => a is "--all");
    bool inherited = args.Any(a => a is "--inherited");
    string[] filteredArgs = args
        .Where(a => a is not ("--quiet" or "-q" or "--context" or "--all" or "--inherited"))
        .ToArray();

    string command = filteredArgs[0];
    string[] rest = filteredArgs[1..];

    return command switch
    {
        "find-refs"      => await FindRefs(rest, quiet, context, all),
        "find-impl"      => await FindImpl(rest, quiet, context),
        "find-ctor"      => await FindCtor(rest, quiet, context),
        "find-overrides" => await FindOverrides(rest, quiet, context, all),
        "find-attribute" => await FindAttribute(rest, quiet, context),
        "find-callers"   => await FindCallers(rest, quiet, context, all),
        "find-base"      => await FindBase(rest, quiet),
        "find-unused"    => await FindUnused(rest, quiet, context),
        "list-members"   => await ListMembers(rest, quiet, inherited, all),
        "list-types"     => await ListTypes(rest, quiet, context),
        _ => await FailAsync($"Unknown command: {command}")
    };
}

static async Task PrintUsageAsync()
{
    TextWriter stderr = Console.Error;
    await stderr.WriteLineAsync("Usage: roslyn-query <command> <symbol> [solution.sln] [flags]");
    await stderr.WriteLineAsync();
    await stderr.WriteLineAsync("Commands:");
    await stderr.WriteLineAsync("  find-refs <Symbol>         All references to a type, property, or method");
    await stderr.WriteLineAsync("  find-callers <Symbol>      All invocation call sites of a method");
    await stderr.WriteLineAsync("  find-impl <Type>           All implementations/subclasses of an interface or class");
    await stderr.WriteLineAsync("  find-ctor <Type>           All constructor call sites (new X(...))");
    await stderr.WriteLineAsync("  find-overrides <Member>    All overrides of a virtual/abstract member");
    await stderr.WriteLineAsync("  find-attribute <Attr>      All symbols decorated with an attribute");
    await stderr.WriteLineAsync("  find-base <Type>           Inheritance chain and implemented interfaces");
    await stderr.WriteLineAsync("  find-unused                All symbols with zero references");
    await stderr.WriteLineAsync("  list-members <Type>        All members of a type (properties, methods, fields)");
    await stderr.WriteLineAsync("  list-types <Namespace>     All types in a namespace (prefix match)");
    await stderr.WriteLineAsync();
    await stderr.WriteLineAsync("Flags:");
    await stderr.WriteLineAsync("  --quiet, -q                Suppress workspace loading warnings");
    await stderr.WriteLineAsync("  --context                  Show trimmed source line alongside file:line output");
    await stderr.WriteLineAsync("  --all                      Return results for all matching symbols when ambiguous");
    await stderr.WriteLineAsync("  --inherited                Include inherited members in list-members output");
    await stderr.WriteLineAsync();
    await stderr.WriteLineAsync("If solution path is omitted, searches parent directories for a .sln file.");
    await stderr.WriteLineAsync("Symbol format: TypeName  or  TypeName.MemberName");
}

static string FormatLocation(FileLinePositionSpan span, bool context, SyntaxTree? tree)
    => LocationFormatter.Format(span, context, tree);

static async Task<int> FailAsync(string message)
{
    await Console.Error.WriteLineAsync($"error: {message}");
    return 1;
}

static string? DiscoverSolution()
{
    string? dir = Directory.GetCurrentDirectory();
    while (!string.IsNullOrEmpty(dir))
    {
        string[] slns = Directory.GetFiles(dir, "*.sln");
        if (slns.Length == 1)
        {
            return slns[0];
        }
        if (slns.Length > 1)
        {
            Console.Error.WriteLine($"Multiple .sln files in {dir} — specify one explicitly.");
            return null;
        }
        dir = Path.GetDirectoryName(dir);
    }
    Console.Error.WriteLine("No .sln file found in current or parent directories.");
    return null;
}

static async Task<MSBuildWorkspace> OpenWorkspace(string solutionPath, bool quiet)
{
    MSBuildWorkspace workspace = MSBuildWorkspace.Create();
    workspace.WorkspaceFailed += (_, e) =>
    {
        if (!quiet && e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
        {
            Console.Error.WriteLine($"workspace warning: {e.Diagnostic.Message}");
        }
    };
    await workspace.OpenSolutionAsync(solutionPath);
    return workspace;
}

static async Task<List<ISymbol>> FindSymbolsByName(Solution solution, string symbolName)
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

static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
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

static IEnumerable<INamedTypeSymbol> GetNestedTypes(INamedTypeSymbol type)
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

static async Task<INamedTypeSymbol?> FindTypeByName(Solution solution, string typeName)
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

static async Task<int> FindRefs(string[] args, bool quiet, bool context, bool all)
{
    if (args.Length == 0)
    {
        return await FailAsync("find-refs requires a symbol name");
    }

    string symbolName = args[0];
    string? solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null)
    {
        return 1;
    }

    using MSBuildWorkspace workspace = await OpenWorkspace(solutionPath, quiet);
    Solution solution = workspace.CurrentSolution;

    List<ISymbol> candidates = await FindSymbolsByName(solution, symbolName);
    SymbolResolverResult resolved = SymbolResolver.ResolveOrAll(
        candidates,
        symbolName,
        all,
        Console.Error);
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
            await Console.Out.WriteLineAsync($"# {symbol.ToDisplayString()}");
        }

        IEnumerable<ReferencedSymbol> refs = await SymbolFinder.FindReferencesAsync(symbol, solution);
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
            await Console.Out.WriteLineAsync(FormatLocation(span, context, loc.SourceTree));
            totalCount++;
        }
    }

    if (totalCount == 0 && !multipleSymbols)
    {
        await Console.Error.WriteLineAsync("No references found.");
    }

    return 0;
}

// -- find-impl ----------------------------------------------------------------

static async Task<int> FindImpl(string[] args, bool quiet, bool context)
{
    if (args.Length == 0)
    {
        return await FailAsync("find-impl requires a type name");
    }

    string typeName = args[0];
    string? solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null)
    {
        return 1;
    }

    using MSBuildWorkspace workspace = await OpenWorkspace(solutionPath, quiet);
    Solution solution = workspace.CurrentSolution;

    INamedTypeSymbol? target = await FindTypeByName(solution, typeName);
    if (target is null)
    {
        return await FailAsync($"Type not found: {typeName}");
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
        string location = FormatLocation(span, context, loc.SourceTree);
        await Console.Out.WriteLineAsync($"{location}\t{impl.ToDisplayString()}");
    }

    return 0;
}

// -- find-ctor ----------------------------------------------------------------

static async Task<int> FindCtor(string[] args, bool quiet, bool context)
{
    if (args.Length == 0)
    {
        return await FailAsync("find-ctor requires a type name");
    }

    string typeName = args[0];
    string? solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null)
    {
        return 1;
    }

    using MSBuildWorkspace workspace = await OpenWorkspace(solutionPath, quiet);
    Solution solution = workspace.CurrentSolution;

    INamedTypeSymbol? target = await FindTypeByName(solution, typeName);
    if (target is null)
    {
        return await FailAsync($"Type not found: {typeName}");
    }

    int count = 0;
    HashSet<string> seen = new();

    foreach (IMethodSymbol ctor in target.Constructors)
    {
        IEnumerable<ReferencedSymbol> refs = await SymbolFinder.FindReferencesAsync(ctor, solution);
        IEnumerable<Location> allLocations = refs
            .SelectMany(r => r.Locations)
            .Select(l => l.Location);
        foreach (Location loc in allLocations)
        {
            FileLinePositionSpan span = loc.GetLineSpan();
            string key = $"{span.Path}:{span.StartLinePosition.Line + 1}";
            if (seen.Add(key))
            {
                await Console.Out.WriteLineAsync(FormatLocation(span, context, loc.SourceTree));
                count++;
            }
        }
    }

    if (count == 0)
    {
        await Console.Error.WriteLineAsync("No constructor call sites found.");
    }

    return 0;
}

// -- find-overrides -----------------------------------------------------------

static async Task<int> FindOverrides(string[] args, bool quiet, bool context, bool all)
{
    if (args.Length == 0)
    {
        return await FailAsync("find-overrides requires a member name");
    }

    string symbolName = args[0];
    string? solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null)
    {
        return 1;
    }

    using MSBuildWorkspace workspace = await OpenWorkspace(solutionPath, quiet);
    Solution solution = workspace.CurrentSolution;

    List<ISymbol> candidates = await FindSymbolsByName(solution, symbolName);
    List<ISymbol> overridable = candidates
        .Where(s => s is IMethodSymbol m && (m.IsVirtual || m.IsAbstract || m.IsOverride)
                 || s is IPropertySymbol p && (p.IsVirtual || p.IsAbstract || p.IsOverride))
        .ToList();

    if (overridable.Count == 0 && candidates.Count > 0)
    {
        return await FailAsync($"'{symbolName}' is not virtual or abstract");
    }

    SymbolResolverResult resolved = SymbolResolver.ResolveOrAll(
        overridable,
        symbolName,
        all,
        Console.Error);
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
            await Console.Out.WriteLineAsync($"# {symbol.ToDisplayString()}");
        }

        IEnumerable<ISymbol> overrides = await SymbolFinder.FindOverridesAsync(symbol, solution);
        foreach (ISymbol o in overrides)
        {
            Location? loc = o.Locations.FirstOrDefault(l => l.IsInSource);
            if (loc is null)
            {
                continue;
            }
            FileLinePositionSpan span = loc.GetLineSpan();
            string location = FormatLocation(span, context, loc.SourceTree);
            await Console.Out.WriteLineAsync(
                $"{location}\t{o.ContainingType?.ToDisplayString()}.{o.Name}");
        }
    }

    return 0;
}

// -- find-attribute -----------------------------------------------------------

static async Task<int> FindAttribute(string[] args, bool quiet, bool context)
{
    if (args.Length == 0)
    {
        return await FailAsync("find-attribute requires an attribute name");
    }

    string attrName = args[0].Trim('[', ']');
    string? solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null)
    {
        return 1;
    }

    using MSBuildWorkspace workspace = await OpenWorkspace(solutionPath, quiet);
    Solution solution = workspace.CurrentSolution;

    Compilation?[] compilations = await Task.WhenAll(
        solution.Projects.Select(p => p.GetCompilationAsync()));

    ConcurrentBag<AttributeMatch> bag = [];
    Parallel.ForEach(
        compilations.Where(c => c is not null).Cast<Compilation>(),
        compilation =>
        {
            IReadOnlyList<AttributeMatch> matches = AttributeScanner.ScanCompilation(compilation, attrName);
            foreach (AttributeMatch match in matches)
            {
                bag.Add(match);
            }
        });

    IReadOnlyList<AttributeMatch> results = AttributeScanner.DeduplicateAndSort([.. bag]);

    foreach (AttributeMatch result in results)
    {
        string location = FormatLocation(result.Span, context, result.Tree);
        await Console.Out.WriteLineAsync($"{location}\t{result.FullyQualifiedName}");
    }

    if (results.Count == 0)
    {
        await Console.Error.WriteLineAsync($"No symbols found with attribute '{attrName}'.");
    }

    return 0;
}

// -- find-callers -------------------------------------------------------------

static async Task<int> FindCallers(string[] args, bool quiet, bool context, bool all)
{
    if (args.Length == 0)
    {
        return await FailAsync("find-callers requires a symbol name");
    }

    string symbolName = args[0];
    string? solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null)
    {
        return 1;
    }

    using MSBuildWorkspace workspace = await OpenWorkspace(solutionPath, quiet);
    Solution solution = workspace.CurrentSolution;

    List<ISymbol> candidates = await FindSymbolsByName(solution, symbolName);
    SymbolResolverResult resolved = SymbolResolver.ResolveOrAll(
        candidates,
        symbolName,
        all,
        Console.Error);
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
            await Console.Out.WriteLineAsync($"# {symbol.ToDisplayString()}");
        }

        IEnumerable<SymbolCallerInfo> callers = await SymbolFinder.FindCallersAsync(symbol, solution);
        IReadOnlyList<SymbolCallerInfo> directCallers = CallerFilter.GetDirectCallers(callers);

        foreach (SymbolCallerInfo caller in directCallers)
        {
            foreach (Location location in caller.Locations)
            {
                FileLinePositionSpan span = location.GetLineSpan();
                string formatted = FormatLocation(span, context, location.SourceTree);
                await Console.Out.WriteLineAsync(
                    $"{formatted}\t{caller.CallingSymbol.ToDisplayString()}");
                totalCount++;
            }
        }
    }

    if (totalCount == 0 && !multipleSymbols)
    {
        await Console.Error.WriteLineAsync("No callers found.");
    }

    return 0;
}

// -- find-unused --------------------------------------------------------------

static async Task<int> FindUnused(string[] args, bool quiet, bool context)
{
    string? solutionPath = args.Length > 0 ? args[0] : DiscoverSolution();
    if (solutionPath is null)
    {
        return 1;
    }

    using MSBuildWorkspace workspace = await OpenWorkspace(solutionPath, quiet);
    Solution solution = workspace.CurrentSolution;

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
                if (UnusedSymbolFilter.ShouldExclude(symbol, interfaceImplementingSymbols))
                {
                    continue;
                }

                string key = symbol.ToDisplayString();
                if (!seen.Add(key))
                {
                    continue;
                }

                IEnumerable<ReferencedSymbol> refs = await SymbolFinder.FindReferencesAsync(
                    symbol,
                    solution);
                bool hasNonDeclarationReference = refs
                    .SelectMany(r => r.Locations)
                    .Select(l => l.Location)
                    .Any(loc => !DeclarationFilter.IsDeclarationSite(
                        loc.SourceTree,
                        loc.SourceSpan,
                        symbol.Locations));

                if (!hasNonDeclarationReference)
                {
                    Location? loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
                    if (loc is not null)
                    {
                        FileLinePositionSpan span = loc.GetLineSpan();
                        string location = FormatLocation(span, context, loc.SourceTree);
                        await Console.Out.WriteLineAsync(
                            $"{location}\t{symbol.ToDisplayString()}");
                        count++;
                    }
                }
            }
        }
    }

    if (count == 0)
    {
        await Console.Error.WriteLineAsync("No unused symbols found.");
    }

    return 0;
}

// -- find-base ----------------------------------------------------------------

static async Task<int> FindBase(string[] args, bool quiet)
{
    if (args.Length == 0)
    {
        return await FailAsync("find-base requires a type name");
    }

    string typeName = args[0];
    string? solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null)
    {
        return 1;
    }

    using MSBuildWorkspace workspace = await OpenWorkspace(solutionPath, quiet);
    Solution solution = workspace.CurrentSolution;

    INamedTypeSymbol? target = await FindTypeByName(solution, typeName);
    if (target is null)
    {
        return await FailAsync($"Type not found: {typeName}");
    }

    INamedTypeSymbol? baseType = target.BaseType;
    while (baseType is not null && baseType.SpecialType != SpecialType.System_Object)
    {
        Location? loc = baseType.Locations.FirstOrDefault(l => l.IsInSource);
        string src = loc is not null
            ? $"{loc.GetLineSpan().Path}:{loc.GetLineSpan().StartLinePosition.Line + 1}"
            : "(external)";
        await Console.Out.WriteLineAsync($"base\t{baseType.ToDisplayString()}\t{src}");
        baseType = baseType.BaseType;
    }

    foreach (INamedTypeSymbol iface in target.AllInterfaces)
    {
        Location? loc = iface.Locations.FirstOrDefault(l => l.IsInSource);
        string src = loc is not null
            ? $"{loc.GetLineSpan().Path}:{loc.GetLineSpan().StartLinePosition.Line + 1}"
            : "(external)";
        await Console.Out.WriteLineAsync($"interface\t{iface.ToDisplayString()}\t{src}");
    }

    return 0;
}

// -- list-members -------------------------------------------------------------

static async Task<int> ListMembers(string[] args, bool quiet, bool inherited, bool all)
{
    if (args.Length == 0)
    {
        return await FailAsync("list-members requires a type name");
    }

    string typeName = args[0];
    string? solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null)
    {
        return 1;
    }

    using MSBuildWorkspace workspace = await OpenWorkspace(solutionPath, quiet);
    Solution solution = workspace.CurrentSolution;

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

        targets = MetadataTypeResolver.FindMetadataTypes(compilations, typeName).ToList();

        if (targets.Count == 0)
        {
            return await FailAsync($"Type not found: {typeName}");
        }

        if (targets.Count > 1 && !all)
        {
            await Console.Error.WriteLineAsync(
                $"Ambiguous '{typeName}' — {targets.Count} matches:");
            foreach (INamedTypeSymbol t in targets)
            {
                await Console.Error.WriteLineAsync($"  {t.ToDisplayString()}");
            }
            await Console.Error.WriteLineAsync(
                "Use a fully-qualified name to disambiguate, or pass --all.");
            return 1;
        }
    }

    bool multipleTypes = targets.Count > 1;

    foreach (INamedTypeSymbol t in targets)
    {
        if (multipleTypes)
        {
            await Console.Out.WriteLineAsync($"# {t.ToDisplayString()}");
        }

        IReadOnlyList<string> lines = MemberFormatter.FormatMembers(t, inherited);
        foreach (string line in lines)
        {
            await Console.Out.WriteLineAsync(line);
        }
    }

    return 0;
}

// -- list-types ---------------------------------------------------------------

static async Task<int> ListTypes(string[] args, bool quiet, bool context)
{
    if (args.Length == 0)
    {
        return await FailAsync("list-types requires a namespace");
    }

    string namespaceName = args[0];
    string? solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null)
    {
        return 1;
    }

    using MSBuildWorkspace workspace = await OpenWorkspace(solutionPath, quiet);
    Solution solution = workspace.CurrentSolution;

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
            string location = FormatLocation(span, context, loc.SourceTree);
            string typeKind = type.TypeKind switch
            {
                TypeKind.Class => "class",
                TypeKind.Interface => "interface",
                TypeKind.Struct => "struct",
                TypeKind.Enum => "enum",
                TypeKind.Delegate => "delegate",
                TypeKind.Module => "module",
                _ => type.TypeKind.ToString()
            };
            await Console.Out.WriteLineAsync($"{typeKind}\t{type.ToDisplayString()}\t{location}");
            count++;
        }
    }

    if (count == 0)
    {
        await Console.Error.WriteLineAsync($"No types found in namespace '{namespaceName}'.");
    }

    return 0;
}
