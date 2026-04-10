using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;

MSBuildLocator.RegisterDefaults();
return await Run(args);

static async Task<int> Run(string[] args)
{
    if (args.Length == 0)
    {
        PrintUsage();
        return 1;
    }

    bool quiet = args.Any(a => a is "--quiet" or "-q");
    bool context = args.Any(a => a is "--context");
    bool all = args.Any(a => a is "--all");
    bool inherited = args.Any(a => a is "--inherited");
    args = args
        .Where(a => a is not ("--quiet" or "-q" or "--context" or "--all" or "--inherited"))
        .ToArray();

    var command = args[0];
    var rest = args[1..];

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
        "list-members"   => await ListMembers(rest, quiet, inherited),
        "list-types"     => await ListTypes(rest, quiet, context),
        _ => Fail($"Unknown command: {command}")
    };
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage: roslyn-query <command> <symbol> [solution.sln] [flags]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Commands:");
    Console.Error.WriteLine("  find-refs <Symbol>         All references to a type, property, or method");
    Console.Error.WriteLine("  find-callers <Symbol>      All invocation call sites of a method");
    Console.Error.WriteLine("  find-impl <Type>           All implementations/subclasses of an interface or class");
    Console.Error.WriteLine("  find-ctor <Type>           All constructor call sites (new X(...))");
    Console.Error.WriteLine("  find-overrides <Member>    All overrides of a virtual/abstract member");
    Console.Error.WriteLine("  find-attribute <Attr>      All symbols decorated with an attribute");
    Console.Error.WriteLine("  find-base <Type>           Inheritance chain and implemented interfaces");
    Console.Error.WriteLine("  find-unused                All symbols with zero references");
    Console.Error.WriteLine("  list-members <Type>        All members of a type (properties, methods, fields)");
    Console.Error.WriteLine("  list-types <Namespace>     All types in a namespace (prefix match)");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Flags:");
    Console.Error.WriteLine("  --quiet, -q                Suppress workspace loading warnings");
    Console.Error.WriteLine("  --context                  Show trimmed source line alongside file:line output");
    Console.Error.WriteLine("  --all                      Return results for all matching symbols when ambiguous");
    Console.Error.WriteLine("  --inherited                Include inherited members in list-members output");
    Console.Error.WriteLine();
    Console.Error.WriteLine("If solution path is omitted, searches parent directories for a .sln file.");
    Console.Error.WriteLine("Symbol format: TypeName  or  TypeName.MemberName");
}

static string FormatLocation(FileLinePositionSpan span, bool context, SyntaxTree? tree)
    => LocationFormatter.Format(span, context, tree);

static int Fail(string message)
{
    Console.Error.WriteLine($"error: {message}");
    return 1;
}

static string? DiscoverSolution()
{
    var dir = Directory.GetCurrentDirectory();
    while (!string.IsNullOrEmpty(dir))
    {
        var slns = Directory.GetFiles(dir, "*.sln");
        if (slns.Length == 1) return slns[0];
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
    var workspace = MSBuildWorkspace.Create();
    workspace.WorkspaceFailed += (_, e) =>
    {
        if (!quiet && e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            Console.Error.WriteLine($"workspace warning: {e.Diagnostic.Message}");
    };
    await workspace.OpenSolutionAsync(solutionPath);
    return workspace;
}

static async Task<List<ISymbol>> FindSymbolsByName(Solution solution, string symbolName)
{
    var parts = symbolName.Split('.', 2);
    var memberName = parts[^1];
    var typeName = parts.Length > 1 ? parts[0] : null;

    var found = new List<ISymbol>();
    var seen = new HashSet<string>();

    foreach (var project in solution.Projects)
    {
        var compilation = await project.GetCompilationAsync();
        if (compilation is null) continue;

        var candidates = compilation.GetSymbolsWithName(name => name == memberName, SymbolFilter.All);

        foreach (var symbol in candidates)
        {
            if (typeName is not null && symbol.ContainingType?.Name != typeName)
                continue;

            var key = symbol.ToDisplayString();
            if (seen.Add(key))
                found.Add(symbol);
        }
    }

    return found;
}

static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
{
    foreach (var type in ns.GetTypeMembers())
    {
        yield return type;
        foreach (var nested in GetNestedTypes(type))
            yield return nested;
    }
    foreach (var childNs in ns.GetNamespaceMembers())
        foreach (var type in GetAllTypes(childNs))
            yield return type;
}

static IEnumerable<INamedTypeSymbol> GetNestedTypes(INamedTypeSymbol type)
{
    foreach (var nested in type.GetTypeMembers())
    {
        yield return nested;
        foreach (var n in GetNestedTypes(nested))
            yield return n;
    }
}

static async Task<INamedTypeSymbol?> FindTypeByName(Solution solution, string typeName)
{
    foreach (var project in solution.Projects)
    {
        var compilation = await project.GetCompilationAsync();
        if (compilation is null) continue;

        var target = compilation
            .GetSymbolsWithName(typeName, SymbolFilter.Type)
            .OfType<INamedTypeSymbol>()
            .FirstOrDefault(t => t.Locations.Any(l => l.IsInSource));

        if (target is not null) return target;
    }
    return null;
}

// ── find-refs ────────────────────────────────────────────────────────────────

static async Task<int> FindRefs(string[] args, bool quiet, bool context, bool all)
{
    if (args.Length == 0)
        return Fail("find-refs requires a symbol name");

    var symbolName = args[0];
    var solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null) return 1;

    using var workspace = await OpenWorkspace(solutionPath, quiet);
    var solution = workspace.CurrentSolution;

    var candidates = await FindSymbolsByName(solution, symbolName);
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
    var totalCount = 0;

    foreach (ISymbol symbol in resolved.Symbols)
    {
        if (multipleSymbols)
        {
            Console.WriteLine($"# {symbol.ToDisplayString()}");
        }

        var refs = await SymbolFinder.FindReferencesAsync(symbol, solution);
        foreach (var refGroup in refs)
        {
            foreach (var location in refGroup.Locations)
            {
                if (DeclarationFilter.IsDeclarationSite(
                    location.Location.SourceTree,
                    location.Location.SourceSpan,
                    symbol.Locations))
                {
                    continue;
                }

                FileLinePositionSpan span = location.Location.GetLineSpan();
                Console.WriteLine(FormatLocation(span, context, location.Location.SourceTree));
                totalCount++;
            }
        }
    }

    if (totalCount == 0 && !multipleSymbols)
    {
        Console.Error.WriteLine("No references found.");
    }

    return 0;
}

// ── find-impl ────────────────────────────────────────────────────────────────

static async Task<int> FindImpl(string[] args, bool quiet, bool context)
{
    if (args.Length == 0)
        return Fail("find-impl requires a type name");

    var typeName = args[0];
    var solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null) return 1;

    using var workspace = await OpenWorkspace(solutionPath, quiet);
    var solution = workspace.CurrentSolution;

    var target = await FindTypeByName(solution, typeName);
    if (target is null)
        return Fail($"Type not found: {typeName}");

    IEnumerable<INamedTypeSymbol> results = target.TypeKind == TypeKind.Interface
        ? await SymbolFinder.FindImplementationsAsync(target, solution)
        : await SymbolFinder.FindDerivedClassesAsync(target, solution);

    foreach (var impl in results)
    {
        Location? loc = impl.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc is null) continue;
        FileLinePositionSpan span = loc.GetLineSpan();
        string location = FormatLocation(span, context, loc.SourceTree);
        Console.WriteLine($"{location}\t{impl.ToDisplayString()}");
    }

    return 0;
}

// ── find-ctor ────────────────────────────────────────────────────────────────

static async Task<int> FindCtor(string[] args, bool quiet, bool context)
{
    if (args.Length == 0)
        return Fail("find-ctor requires a type name");

    var typeName = args[0];
    var solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null) return 1;

    using var workspace = await OpenWorkspace(solutionPath, quiet);
    var solution = workspace.CurrentSolution;

    var target = await FindTypeByName(solution, typeName);
    if (target is null)
        return Fail($"Type not found: {typeName}");

    var count = 0;
    var seen = new HashSet<string>();

    foreach (var ctor in target.Constructors)
    {
        var refs = await SymbolFinder.FindReferencesAsync(ctor, solution);
        foreach (var refGroup in refs)
        {
            foreach (var location in refGroup.Locations)
            {
                FileLinePositionSpan span = location.Location.GetLineSpan();
                string key = $"{span.Path}:{span.StartLinePosition.Line + 1}";
                if (seen.Add(key))
                {
                    Console.WriteLine(FormatLocation(span, context, location.Location.SourceTree));
                    count++;
                }
            }
        }
    }

    if (count == 0)
        Console.Error.WriteLine("No constructor call sites found.");

    return 0;
}

// ── find-overrides ───────────────────────────────────────────────────────────

static async Task<int> FindOverrides(string[] args, bool quiet, bool context, bool all)
{
    if (args.Length == 0)
        return Fail("find-overrides requires a member name");

    var symbolName = args[0];
    var solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null) return 1;

    using var workspace = await OpenWorkspace(solutionPath, quiet);
    var solution = workspace.CurrentSolution;

    var candidates = await FindSymbolsByName(solution, symbolName);
    List<ISymbol> overridable = candidates
        .Where(s => s is IMethodSymbol m && (m.IsVirtual || m.IsAbstract || m.IsOverride)
                 || s is IPropertySymbol p && (p.IsVirtual || p.IsAbstract || p.IsOverride))
        .ToList();

    if (overridable.Count == 0 && candidates.Count > 0)
    {
        return Fail($"'{symbolName}' is not virtual or abstract");
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
            Console.WriteLine($"# {symbol.ToDisplayString()}");
        }

        var overrides = await SymbolFinder.FindOverridesAsync(symbol, solution);
        foreach (ISymbol o in overrides)
        {
            Location? loc = o.Locations.FirstOrDefault(l => l.IsInSource);
            if (loc is null)
            {
                continue;
            }
            FileLinePositionSpan span = loc.GetLineSpan();
            string location = FormatLocation(span, context, loc.SourceTree);
            Console.WriteLine($"{location}\t{o.ContainingType?.ToDisplayString()}.{o.Name}");
        }
    }

    return 0;
}

// ── find-attribute ───────────────────────────────────────────────────────────

static async Task<int> FindAttribute(string[] args, bool quiet, bool context)
{
    if (args.Length == 0)
        return Fail("find-attribute requires an attribute name");

    var attrName = args[0].Trim('[', ']');
    var solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null) return 1;

    using var workspace = await OpenWorkspace(solutionPath, quiet);
    var solution = workspace.CurrentSolution;

    var seen = new HashSet<string>();
    var count = 0;

    foreach (var project in solution.Projects)
    {
        var compilation = await project.GetCompilationAsync();
        if (compilation is null) continue;

        foreach (var type in GetAllTypes(compilation.GlobalNamespace))
        {
            PrintIfAttributed(type, attrName, context, seen, ref count);
            foreach (var member in type.GetMembers())
            {
                PrintIfAttributed(member, attrName, context, seen, ref count);
            }
        }
    }

    if (count == 0)
        Console.Error.WriteLine($"No symbols found with attribute '{attrName}'.");

    return 0;
}

static void PrintIfAttributed(
    ISymbol symbol,
    string attrName,
    bool context,
    HashSet<string> seen,
    ref int count)
{
    bool match = symbol.GetAttributes().Any(a =>
    {
        string? name = a.AttributeClass?.Name;
        if (name is null) return false;
        string bare = name.EndsWith("Attribute") ? name[..^"Attribute".Length] : name;
        return name == attrName || bare == attrName;
    });

    if (!match) return;

    Location? loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
    if (loc is null) return;

    FileLinePositionSpan span = loc.GetLineSpan();
    string key = $"{span.Path}:{span.StartLinePosition.Line + 1}";
    if (!seen.Add(key)) return;

    string location = FormatLocation(span, context, loc.SourceTree);
    Console.WriteLine($"{location}\t{symbol.ToDisplayString()}");
    count++;
}

// ── find-callers ────────────────────────────────────────────────────────────

static async Task<int> FindCallers(string[] args, bool quiet, bool context, bool all)
{
    if (args.Length == 0)
    {
        return Fail("find-callers requires a symbol name");
    }

    var symbolName = args[0];
    var solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null)
    {
        return 1;
    }

    using var workspace = await OpenWorkspace(solutionPath, quiet);
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
            Console.WriteLine($"# {symbol.ToDisplayString()}");
        }

        IEnumerable<SymbolCallerInfo> callers = await SymbolFinder.FindCallersAsync(symbol, solution);
        List<SymbolCallerInfo> directCallers = CallerFilter.GetDirectCallers(callers);

        foreach (SymbolCallerInfo caller in directCallers)
        {
            foreach (Location location in caller.Locations)
            {
                FileLinePositionSpan span = location.GetLineSpan();
                string formatted = FormatLocation(span, context, location.SourceTree);
                Console.WriteLine($"{formatted}\t{caller.CallingSymbol.ToDisplayString()}");
                totalCount++;
            }
        }
    }

    if (totalCount == 0 && !multipleSymbols)
    {
        Console.Error.WriteLine("No callers found.");
    }

    return 0;
}

// ── find-unused ─────────────────────────────────────────────────────────────

static async Task<int> FindUnused(string[] args, bool quiet, bool context)
{
    var solutionPath = args.Length > 0 ? args[0] : DiscoverSolution();
    if (solutionPath is null)
    {
        return 1;
    }

    using var workspace = await OpenWorkspace(solutionPath, quiet);
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

            List<ISymbol> symbols = [type];
            symbols.AddRange(type.GetMembers());

            foreach (ISymbol symbol in symbols)
            {
                if (UnusedSymbolFilter.ShouldExclude(symbol))
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
                bool hasNonDeclarationReference = false;

                foreach (ReferencedSymbol refGroup in refs)
                {
                    foreach (ReferenceLocation location in refGroup.Locations)
                    {
                        if (DeclarationFilter.IsDeclarationSite(
                            location.Location.SourceTree,
                            location.Location.SourceSpan,
                            symbol.Locations))
                        {
                            continue;
                        }

                        hasNonDeclarationReference = true;
                        break;
                    }

                    if (hasNonDeclarationReference)
                    {
                        break;
                    }
                }

                if (!hasNonDeclarationReference)
                {
                    Location? loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
                    if (loc is not null)
                    {
                        FileLinePositionSpan span = loc.GetLineSpan();
                        string location = FormatLocation(span, context, loc.SourceTree);
                        Console.WriteLine($"{location}\t{symbol.ToDisplayString()}");
                        count++;
                    }
                }
            }
        }
    }

    if (count == 0)
    {
        Console.Error.WriteLine("No unused symbols found.");
    }

    return 0;
}

// ── find-base ────────────────────────────────────────────────────────────────

static async Task<int> FindBase(string[] args, bool quiet)
{
    if (args.Length == 0)
        return Fail("find-base requires a type name");

    var typeName = args[0];
    var solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null) return 1;

    using var workspace = await OpenWorkspace(solutionPath, quiet);
    var solution = workspace.CurrentSolution;

    var target = await FindTypeByName(solution, typeName);
    if (target is null)
        return Fail($"Type not found: {typeName}");

    var baseType = target.BaseType;
    while (baseType is not null && baseType.SpecialType != SpecialType.System_Object)
    {
        var loc = baseType.Locations.FirstOrDefault(l => l.IsInSource);
        var src = loc is not null
            ? $"{loc.GetLineSpan().Path}:{loc.GetLineSpan().StartLinePosition.Line + 1}"
            : "(external)";
        Console.WriteLine($"base\t{baseType.ToDisplayString()}\t{src}");
        baseType = baseType.BaseType;
    }

    foreach (var iface in target.AllInterfaces)
    {
        var loc = iface.Locations.FirstOrDefault(l => l.IsInSource);
        var src = loc is not null
            ? $"{loc.GetLineSpan().Path}:{loc.GetLineSpan().StartLinePosition.Line + 1}"
            : "(external)";
        Console.WriteLine($"interface\t{iface.ToDisplayString()}\t{src}");
    }

    return 0;
}

// ── list-members ─────────────────────────────────────────────────────────────

static async Task<int> ListMembers(string[] args, bool quiet, bool inherited)
{
    if (args.Length == 0)
        return Fail("list-members requires a type name");

    var typeName = args[0];
    var solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null) return 1;

    using var workspace = await OpenWorkspace(solutionPath, quiet);
    var solution = workspace.CurrentSolution;

    var target = await FindTypeByName(solution, typeName);
    if (target is null)
        return Fail($"Type not found: {typeName}");

    List<string> lines = MemberFormatter.FormatMembers(target, inherited);
    foreach (string line in lines)
    {
        Console.WriteLine(line);
    }

    return 0;
}

// ── list-types ───────────────────────────────────────────────────────────────

static async Task<int> ListTypes(string[] args, bool quiet, bool context)
{
    if (args.Length == 0)
        return Fail("list-types requires a namespace");

    var namespaceName = args[0];
    var solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null) return 1;

    using var workspace = await OpenWorkspace(solutionPath, quiet);
    var solution = workspace.CurrentSolution;

    var seen = new HashSet<string>();
    var count = 0;

    foreach (var project in solution.Projects)
    {
        var compilation = await project.GetCompilationAsync();
        if (compilation is null) continue;

        foreach (var type in GetAllTypes(compilation.GlobalNamespace))
        {
            if (!type.ContainingNamespace.ToDisplayString().StartsWith(namespaceName)) continue;

            Location? loc = type.Locations.FirstOrDefault(l => l.IsInSource);
            if (loc is null) continue;

            string key = type.ToDisplayString();
            if (!seen.Add(key)) continue;

            FileLinePositionSpan span = loc.GetLineSpan();
            string location = FormatLocation(span, context, loc.SourceTree);
            Console.WriteLine(
                $"{type.TypeKind.ToString().ToLower()}\t{type.ToDisplayString()}\t{location}");
            count++;
        }
    }

    if (count == 0)
        Console.Error.WriteLine($"No types found in namespace '{namespaceName}'.");

    return 0;
}
