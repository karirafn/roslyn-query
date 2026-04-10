using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;

using RoslynQuery;

MSBuildLocator.RegisterDefaults();
return await Run(args);

static async Task<int> Run(string[] args)
{
    if (args.Length == 0)
    {
        await CommandDispatcher.PrintUsageAsync(Console.Error);
        return 1;
    }

    bool quiet = args.Any(a => a is "--quiet" or "-q");

    string? solutionPath = ResolveSolutionPath(args);
    if (solutionPath is null)
    {
        return 1;
    }

    using MSBuildWorkspace workspace = await OpenWorkspace(solutionPath, quiet);
    CommandContext context = new(Console.Out, Console.Error, workspace.CurrentSolution);
    return await CommandDispatcher.ExecuteAsync(args, context);
}

static string? ResolveSolutionPath(string[] args)
{
    string[] nonFlags = args
        .Where(a => !a.StartsWith('-'))
        .ToArray();

    string? explicitPath = nonFlags
        .Skip(1)
        .FirstOrDefault(a => a.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || a.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));

    return explicitPath ?? DiscoverSolution();
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
            Console.Error.WriteLine(
                $"Multiple .sln files in {dir} — specify one explicitly.");
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
        if (!quiet
            && e.Diagnostic.Kind == Microsoft.CodeAnalysis.WorkspaceDiagnosticKind.Failure)
        {
            Console.Error.WriteLine($"workspace warning: {e.Diagnostic.Message}");
        }
    };
    await workspace.OpenSolutionAsync(solutionPath);
    return workspace;
}
