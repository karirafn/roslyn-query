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

    if (args[0] == "--daemon")
    {
        return await RunDaemon(args);
    }

    if (args[0] == "daemon" && args.Length >= 2 && args[1] == "stop")
    {
        return RunDaemonStop(args);
    }

    if (args[0] == "batch")
    {
        return await RunBatch(args);
    }

    return await RunCommand(args);
}

static async Task<int> RunDaemon(string[] args)
{
    if (args.Length < 2)
    {
        await Console.Error.WriteLineAsync("--daemon requires a solution path");
        return 1;
    }

    string solutionPath = Path.GetFullPath(args[1]);
    DaemonProcess.WritePidFile(solutionPath);

    using CancellationTokenSource cts = new();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    await DaemonServer.RunAsync(solutionPath, cts.Token);
    return 0;
}

static int RunDaemonStop(string[] args)
{
    string? solutionPath = args.Length >= 3
        ? Path.GetFullPath(args[2])
        : SolutionDiscovery.Discover(Directory.GetCurrentDirectory(), Console.Error);

    if (solutionPath is null)
    {
        return 1;
    }

    DaemonProcess.StopDaemon(solutionPath);
    return 0;
}

static async Task<int> RunCommand(string[] args)
{
    bool quiet = args.Any(a => a is "--quiet" or "-q");

    string? solutionPath = ResolveSolutionPath(args);
    if (solutionPath is null)
    {
        return 1;
    }

    int? daemonResult = await DaemonClient.TryExecuteAsync(
        solutionPath,
        args,
        Console.Out,
        Console.Error);

    if (daemonResult.HasValue)
    {
        return daemonResult.Value;
    }

    DaemonProcess.StartDaemon(solutionPath);
    daemonResult = await PollDaemon(solutionPath, args);

    if (daemonResult.HasValue)
    {
        return daemonResult.Value;
    }

    return await RunDirect(solutionPath, args, quiet);
}

static async Task<int?> PollDaemon(string solutionPath, string[] args)
{
    const int pollIntervalMs = 500;
    const int maxWaitMs = 15_000;
    int elapsed = 0;

    while (elapsed < maxWaitMs)
    {
        await Task.Delay(pollIntervalMs);
        elapsed += pollIntervalMs;

        int? result = await DaemonClient.TryExecuteAsync(
            solutionPath,
            args,
            Console.Out,
            Console.Error);

        if (result.HasValue)
        {
            return result;
        }
    }

    return null;
}

static async Task<int> RunBatch(string[] args)
{
    string[] globalFlags = args[1..]
        .Where(a => a.StartsWith('-'))
        .ToArray();

    string? solutionPath = ResolveSolutionPath(args);
    if (solutionPath is null)
    {
        return 1;
    }

    string? filePath = BatchFileReader.ResolveFilePath(args);
    if (filePath is not null && !File.Exists(filePath))
    {
        await Console.Error.WriteLineAsync($"error: file not found: {filePath}");
        return 1;
    }

    DaemonProcess.StartDaemon(solutionPath);

    StreamReader? fileReader = filePath is not null
        ? new StreamReader(filePath, System.Text.Encoding.UTF8)
        : null;
    TextReader reader = fileReader ?? Console.In;

    try
    {
        string? line;
        int lastExitCode = 0;

        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            await Console.Out.WriteLineAsync($"=== {line} ===");

            string[] subArgs = LineTokenizer.Tokenize(line);
            string[] fullArgs = [.. globalFlags, .. subArgs];

            int? daemonResult = await DaemonClient.TryExecuteAsync(
                solutionPath,
                fullArgs,
                Console.Out,
                Console.Error);

            if (daemonResult.HasValue)
            {
                lastExitCode = daemonResult.Value;
                continue;
            }

            daemonResult = await PollDaemon(solutionPath, fullArgs);
            if (daemonResult.HasValue)
            {
                lastExitCode = daemonResult.Value;
                continue;
            }

            await Console.Error.WriteLineAsync(
                $"error: daemon unavailable for command: {line}");
            lastExitCode = 1;
        }

        return lastExitCode;
    }
    finally
    {
        fileReader?.Dispose();
    }
}

static async Task<int> RunDirect(string solutionPath, string[] args, bool quiet)
{
    using MSBuildWorkspace workspace = await OpenWorkspace(solutionPath, quiet);
    string solutionDirectory = Path.GetDirectoryName(solutionPath) ?? "";
    CommandContext context = new(
        Console.Out,
        Console.Error,
        workspace.CurrentSolution,
        solutionDirectory);
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

    if (explicitPath is not null)
    {
        return Path.GetFullPath(explicitPath);
    }

    return SolutionDiscovery.Discover(Directory.GetCurrentDirectory(), Console.Error);
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
