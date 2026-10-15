using System.Diagnostics;

using AppHostLocator = roslyn_query.Tests.ColdStartIntegrationTests.AppHostLocator;

namespace roslyn_query.Tests.DaemonIntegrationTests;

/// <summary>
/// Shared setup and helpers for integration tests that spawn a real daemon process
/// against a per-class copy of the MinimalSolution fixture.
///
/// Each class gets a unique fixture directory and ROSLYN_QUERY_STATE_DIR, so the
/// derived pipe name never collides with other test classes running in parallel.
/// </summary>
internal sealed class DaemonTestFixture
{
    internal string FileName { get; }
    internal IReadOnlyList<string> PrefixArgs { get; }
    internal string FixtureSlnxPath { get; }
    internal string FixtureDir { get; }
    internal string StateDir { get; }
    internal string SocketDir { get; }
    internal bool Skip { get; }

    /// <summary>
    /// Initialises the fixture using a unique directory prefix (e.g. "stale-edits")
    /// so log output and temp paths are identifiable per class.
    /// </summary>
    internal DaemonTestFixture(string dirPrefix)
    {
        if (!AppHostLocator.TryLocate(out string fileName, out IReadOnlyList<string> prefixArgs))
        {
            Skip = true;
            FileName = string.Empty;
            PrefixArgs = [];
            FixtureSlnxPath = string.Empty;
            FixtureDir = string.Empty;
            StateDir = string.Empty;
            SocketDir = string.Empty;
            return;
        }

        FileName = fileName;
        PrefixArgs = prefixArgs;

        FixtureDir = Path.Combine(Path.GetTempPath(), $"rq-{dirPrefix}-fix-{Guid.NewGuid():N}");
        StateDir = Path.Combine(Path.GetTempPath(), $"rq-{dirPrefix}-state-{Guid.NewGuid():N}");
        // Per-class socket dir so parallel test classes do not collide on the socket path and
        // so PrepareSocketDirectory always creates a fresh 0700 directory (never a stale one).
        SocketDir = Path.Combine(Path.GetTempPath(), $"rq-{dirPrefix}-sock-{Guid.NewGuid():N}");
        FixtureSlnxPath = Path.Combine(FixtureDir, "MinimalSolution.slnx");
    }

    /// <summary>Copies the MinimalSolution fixture to FixtureDir and creates StateDir.</summary>
    internal void CopyFixture()
    {
        CopyFixtureToDir(FixtureDir);
        Directory.CreateDirectory(StateDir);
    }

    /// <summary>Cleans up the per-class fixture, state, and socket directories.</summary>
    internal void Cleanup()
    {
        CleanupDirectory(FixtureDir);
        CleanupDirectory(StateDir);
        CleanupDirectory(SocketDir);
    }

    /// <summary>
    /// Sends a daemon-stop command for the per-class fixture path.
    /// Safe to call when no daemon is running — stop exits cleanly in that case.
    /// </summary>
    internal async Task StopDaemonAsync()
    {
        ProcessStartInfo stopInfo = BuildStartInfo(
            ["daemon", "stop", FixtureSlnxPath]);

        using Process stop = Process.Start(stopInfo)
            ?? throw new InvalidOperationException("Failed to start roslyn-query to stop daemon.");

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
        await stop.WaitForExitAsync(cts.Token);
    }

    /// <summary>
    /// Builds a ProcessStartInfo for the roslyn-query client, with ROSLYN_QUERY_STATE_DIR
    /// set to this fixture's state directory. The command args are appended after the
    /// binary's prefix args.
    /// </summary>
    internal ProcessStartInfo BuildClientStartInfo(IReadOnlyList<string> commandArgs)
    {
        ProcessStartInfo startInfo = BuildStartInfo(commandArgs);
        return startInfo;
    }

    /// <summary>
    /// Runs a roslyn-query command, discards stdout/stderr, and returns the exit code.
    /// </summary>
    internal async Task<int> RunClientAsync(
        IReadOnlyList<string> commandArgs,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = BuildClientStartInfo(commandArgs);

        using Process client = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start roslyn-query client.");

        // Read both streams concurrently to prevent pipe-buffer deadlocks.
        Task<string> stdoutTask = client.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = client.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);
        await client.WaitForExitAsync(cancellationToken);
        return client.ExitCode;
    }

    /// <summary>
    /// Runs a roslyn-query command and returns both stdout and the exit code.
    /// </summary>
    internal async Task<(string Stdout, string Stderr, int ExitCode)> RunClientWithOutputAsync(
        IReadOnlyList<string> commandArgs,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = BuildClientStartInfo(commandArgs);

        using Process client = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start roslyn-query client.");

        // Read both streams concurrently to prevent pipe-buffer deadlocks.
        Task<string> stdoutTask = client.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = client.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);
        await client.WaitForExitAsync(cancellationToken);

        return (await stdoutTask, await stderrTask, client.ExitCode);
    }

    private ProcessStartInfo BuildStartInfo(IReadOnlyList<string> commandArgs)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string arg in PrefixArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (string arg in commandArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.EnvironmentVariables["ROSLYN_QUERY_STATE_DIR"] = StateDir;
        // Per-class socket dir ensures PrepareSocketDirectory creates a fresh 0700 directory
        // for each class, preventing stale 0755 dirs from a prior run causing rejection.
        startInfo.EnvironmentVariables["ROSLYN_QUERY_SOCKET_DIR"] = SocketDir;
        return startInfo;
    }

    private static void CopyFixtureToDir(string targetDir)
    {
        string sourceDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MinimalSolution");
        Directory.CreateDirectory(targetDir);

        foreach (string sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDir, sourceFile);
            string destFile = Path.Combine(targetDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(sourceFile, destFile, overwrite: true);
        }
    }

    private static void CleanupDirectory(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        foreach (string subDir in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
            .OrderByDescending(d => d.Length))
        {
            Directory.Delete(subDir);
        }

        Directory.Delete(dir);
    }
}
