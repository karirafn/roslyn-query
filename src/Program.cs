using System.Collections.Frozen;
using System.Diagnostics;

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
        return await RunDaemonStop(args);
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

static async Task<int> RunDaemonStop(string[] args)
{
    if (args.Length >= 3 && args[2] == "--all")
    {
        DaemonProcess.StopAllDaemons();
        return 0;
    }

    string? solutionPath = args.Length >= 3
        ? Path.GetFullPath(args[2])
        : SolutionDiscovery.Discover(Directory.GetCurrentDirectory(), Console.Error);

    if (solutionPath is null)
    {
        return 1;
    }

    await DaemonProcess.StopDaemon(solutionPath);
    return 0;
}

static async Task<int> RunCommand(string[] args)
{
    string? solutionPath = ResolveSolutionPath(args);
    if (solutionPath is null)
    {
        return 1;
    }

    Stopwatch loadingStopwatch = Stopwatch.StartNew();

    return await DaemonCommandRunner.RunAsync(
        solutionPath,
        args,
        Console.Out,
        Console.Error,
        tryExecute: (path, a, o, e, ct) => DaemonClient.TryExecuteAsync(path, a, o, e, ct),
        spawn: path => DaemonProcess.StartDaemon(path),
        kill: path => KillDaemon(path),
        runDirect: (path, a) => RunDirect(path, a),
        clock: () => loadingStopwatch.Elapsed);
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

    StreamReader? fileReader = filePath is not null
        ? new StreamReader(filePath, System.Text.Encoding.UTF8)
        : null;

    try
    {
        IEnumerable<string> lines = fileReader is not null
            ? ReadLinesFrom(fileReader)
            : ReadLinesFrom(Console.In);

        Stopwatch loadingStopwatch = Stopwatch.StartNew();

        return await DaemonCommandRunner.RunBatchAsync(
            solutionPath,
            lines,
            globalFlags,
            Console.Out,
            Console.Error,
            tryExecute: (path, a, o, e, ct) => DaemonClient.TryExecuteAsync(path, a, o, e, ct),
            spawn: path => DaemonProcess.StartDaemon(path),
            kill: path => KillDaemon(path),
            runDirect: (path, a) => RunDirect(path, a),
            clock: () => loadingStopwatch.Elapsed);
    }
    finally
    {
        fileReader?.Dispose();
    }
}

static IEnumerable<string> ReadLinesFrom(TextReader reader)
{
    string? line;
    while ((line = reader.ReadLine()) is not null)
    {
        yield return line;
    }
}

static async Task<int> RunDirect(string solutionPath, string[] args)
{
    (MSBuildWorkspace Workspace, LoadHealth Health) opened = await OpenWorkspace(solutionPath);
    using MSBuildWorkspace workspace = opened.Workspace;
    LoadHealth health = opened.Health;
    string solutionDirectory = Path.GetDirectoryName(solutionPath) ?? "";
    FrozenSet<string> documentPaths = TrackedFiles.CollectDocumentPaths(workspace.CurrentSolution);
    CommandContext context = new(
        Console.Out,
        Console.Error,
        workspace.CurrentSolution,
        solutionDirectory,
        documentPaths,
        health);
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

static async Task<(MSBuildWorkspace Workspace, LoadHealth Health)> OpenWorkspace(string solutionPath)
{
    MSBuildWorkspace workspace = MSBuildWorkspace.Create();
    LoadHealth health = await SolutionLoader.LoadWithHealthAsync(workspace, solutionPath);
    return (workspace, health);
}

static void KillDaemon(string solutionPath)
{
    (int Pid, long? StartTimeTicks)? record = DaemonProcess.ReadPidRecord(solutionPath);
    if (record is null)
    {
        return;
    }

    try
    {
        using Process process = Process.GetProcessById(record.Value.Pid);

        bool isOurDaemon = DaemonProcess.IsDaemonProcess(process);
        bool startTimeMatches = record.Value.StartTimeTicks is long ticks
            && DaemonProcess.IsRecordedDaemon(process, ticks);

        if (isOurDaemon && startTimeMatches)
        {
            process.Kill();
        }
    }
    catch (ArgumentException)
    {
        // Process already exited — nothing to kill
    }
    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        // Process exited between GetProcessById and Kill, or kill was denied — ignore
    }
}
