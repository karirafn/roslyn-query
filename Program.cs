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
    args = args.Where(a => a is not ("--quiet" or "-q")).ToArray();

    var command = args[0];
    var rest = args[1..];

    return command switch
    {
        "find-refs"      => await FindRefs(rest, quiet),
        "find-impl"      => await FindImpl(rest, quiet),
        "find-ctor"      => await FindCtor(rest, quiet),
        "find-overrides" => await FindOverrides(rest, quiet),
        "find-attribute" => await FindAttribute(rest, quiet),
        "find-base"      => await FindBase(rest, quiet),
        "list-members"   => await ListMembers(rest, quiet),
        "list-types"     => await ListTypes(rest, quiet),
        _ => Fail($"Unknown command: {command}")
    };
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage: roslyn-query <command> <symbol> [solution.sln] [--quiet]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Commands:");
    Console.Error.WriteLine("  find-refs <Symbol>         All references to a type, property, or method");
    Console.Error.WriteLine("  find-impl <Type>           All implementations/subclasses of an interface or class");
    Console.Error.WriteLine("  find-ctor <Type>           All constructor call sites (new X(...))");
    Console.Error.WriteLine("  find-overrides <Member>    All overrides of a virtual/abstract member");
    Console.Error.WriteLine("  find-attribute <Attr>      All symbols decorated with an attribute");
    Console.Error.WriteLine("  find-base <Type>           Inheritance chain and implemented interfaces");
    Console.Error.WriteLine("  list-members <Type>        All members of a type (properties, methods, fields)");
    Console.Error.WriteLine("  list-types <Namespace>     All types in a namespace (prefix match)");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Flags:");
    Console.Error.WriteLine("  --quiet, -q                Suppress workspace loading warnings");
    Console.Error.WriteLine();
    Console.Error.WriteLine("If solution path is omitted, searches parent directories for a .sln file.");
    Console.Error.WriteLine("Symbol format: TypeName  or  TypeName.MemberName");
}

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

static async Task<int> FindRefs(string[] args, bool quiet)
{
    if (args.Length == 0)
        return Fail("find-refs requires a symbol name");

    var symbolName = args[0];
    var solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null) return 1;

    using var workspace = await OpenWorkspace(solutionPath, quiet);
    var solution = workspace.CurrentSolution;

    var symbols = await FindSymbolsByName(solution, symbolName);

    if (symbols.Count == 0)
        return Fail($"Symbol not found: {symbolName}");

    if (symbols.Count > 1)
    {
        Console.Error.WriteLine($"Ambiguous '{symbolName}' — {symbols.Count} matches:");
        foreach (var s in symbols)
            Console.Error.WriteLine($"  {s.ToDisplayString()} ({s.Kind})");
        Console.Error.WriteLine("Use TypeName.MemberName to disambiguate.");
        return 1;
    }

    var refs = await SymbolFinder.FindReferencesAsync(symbols[0], solution);
    var count = 0;

    foreach (var refGroup in refs)
    {
        foreach (var location in refGroup.Locations)
        {
            var span = location.Location.GetLineSpan();
            Console.WriteLine($"{span.Path}:{span.StartLinePosition.Line + 1}");
            count++;
        }
    }

    if (count == 0)
        Console.Error.WriteLine("No references found.");

    return 0;
}

// ── find-impl ────────────────────────────────────────────────────────────────

static async Task<int> FindImpl(string[] args, bool quiet)
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
        var loc = impl.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc is null) continue;
        var span = loc.GetLineSpan();
        Console.WriteLine($"{span.Path}:{span.StartLinePosition.Line + 1}\t{impl.ToDisplayString()}");
    }

    return 0;
}

// ── find-ctor ────────────────────────────────────────────────────────────────

static async Task<int> FindCtor(string[] args, bool quiet)
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
                var span = location.Location.GetLineSpan();
                var key = $"{span.Path}:{span.StartLinePosition.Line + 1}";
                if (seen.Add(key))
                {
                    Console.WriteLine(key);
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

static async Task<int> FindOverrides(string[] args, bool quiet)
{
    if (args.Length == 0)
        return Fail("find-overrides requires a member name");

    var symbolName = args[0];
    var solutionPath = args.Length > 1 ? args[1] : DiscoverSolution();
    if (solutionPath is null) return 1;

    using var workspace = await OpenWorkspace(solutionPath, quiet);
    var solution = workspace.CurrentSolution;

    var symbols = await FindSymbolsByName(solution, symbolName);
    if (symbols.Count == 0)
        return Fail($"Symbol not found: {symbolName}");

    var overridable = symbols
        .Where(s => s is IMethodSymbol m && (m.IsVirtual || m.IsAbstract || m.IsOverride)
                 || s is IPropertySymbol p && (p.IsVirtual || p.IsAbstract || p.IsOverride))
        .ToList();

    if (overridable.Count == 0)
        return Fail($"'{symbolName}' is not virtual or abstract");

    if (overridable.Count > 1)
    {
        Console.Error.WriteLine($"Ambiguous — {overridable.Count} matches. Use TypeName.MemberName.");
        return 1;
    }

    var overrides = await SymbolFinder.FindOverridesAsync(overridable[0], solution);
    foreach (var o in overrides)
    {
        var loc = o.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc is null) continue;
        var span = loc.GetLineSpan();
        Console.WriteLine($"{span.Path}:{span.StartLinePosition.Line + 1}\t{o.ContainingType?.ToDisplayString()}.{o.Name}");
    }

    return 0;
}

// ── find-attribute ───────────────────────────────────────────────────────────

static async Task<int> FindAttribute(string[] args, bool quiet)
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
            PrintIfAttributed(type, attrName, seen, ref count);
            foreach (var member in type.GetMembers())
                PrintIfAttributed(member, attrName, seen, ref count);
        }
    }

    if (count == 0)
        Console.Error.WriteLine($"No symbols found with attribute '{attrName}'.");

    return 0;
}

static void PrintIfAttributed(ISymbol symbol, string attrName, HashSet<string> seen, ref int count)
{
    var match = symbol.GetAttributes().Any(a =>
    {
        var name = a.AttributeClass?.Name;
        if (name is null) return false;
        var bare = name.EndsWith("Attribute") ? name[..^"Attribute".Length] : name;
        return name == attrName || bare == attrName;
    });

    if (!match) return;

    var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
    if (loc is null) return;

    var span = loc.GetLineSpan();
    var key = $"{span.Path}:{span.StartLinePosition.Line + 1}";
    if (!seen.Add(key)) return;

    Console.WriteLine($"{span.Path}:{span.StartLinePosition.Line + 1}\t{symbol.ToDisplayString()}");
    count++;
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

static async Task<int> ListMembers(string[] args, bool quiet)
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

    foreach (var member in target.GetMembers().OrderBy(m => m.Kind).ThenBy(m => m.Name))
    {
        if (member.IsImplicitlyDeclared) continue;

        var display = member switch
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

        if (display is not null)
            Console.WriteLine(display);
    }

    return 0;
}

// ── list-types ───────────────────────────────────────────────────────────────

static async Task<int> ListTypes(string[] args, bool quiet)
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

            var loc = type.Locations.FirstOrDefault(l => l.IsInSource);
            if (loc is null) continue;

            var key = type.ToDisplayString();
            if (!seen.Add(key)) continue;

            var span = loc.GetLineSpan();
            Console.WriteLine($"{type.TypeKind.ToString().ToLower()}\t{type.ToDisplayString()}\t{span.Path}:{span.StartLinePosition.Line + 1}");
            count++;
        }
    }

    if (count == 0)
        Console.Error.WriteLine($"No types found in namespace '{namespaceName}'.");

    return 0;
}
