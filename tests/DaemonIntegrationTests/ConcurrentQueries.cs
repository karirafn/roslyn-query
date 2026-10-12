using System.Diagnostics;

using Shouldly;

using AppHostLocator = roslyn_query.Tests.ColdStartIntegrationTests.AppHostLocator;

namespace roslyn_query.Tests.DaemonIntegrationTests;

/// <summary>
/// Real-apphost integration test: two client processes query the same daemon concurrently.
/// Both must succeed (exit 0), proving that the concurrent-accept loop serves multiple
/// connections from one snapshot.
/// </summary>
public sealed class ConcurrentQueries : IAsyncLifetime
{
    private const int DeadlineSeconds = 120;

    private readonly string _fixtureSlnxPath;
    private readonly string _fixtureDir;
    private readonly string _stateDir;
    private readonly string _fileName;
    private readonly IReadOnlyList<string> _prefixArgs;
    private readonly bool _skip;

    public ConcurrentQueries()
    {
        if (!AppHostLocator.TryLocate(out string fileName, out IReadOnlyList<string> prefixArgs))
        {
            _skip = true;
            _fileName = string.Empty;
            _prefixArgs = [];
            _fixtureSlnxPath = string.Empty;
            _fixtureDir = string.Empty;
            _stateDir = string.Empty;
            return;
        }

        _fileName = fileName;
        _prefixArgs = prefixArgs;

        // Unique per-class fixture copy so the derived pipe name does not collide with other test classes.
        _fixtureDir = Path.Combine(Path.GetTempPath(), $"rq-concurrent-fix-{Guid.NewGuid():N}");
        _stateDir = Path.Combine(Path.GetTempPath(), $"rq-concurrent-state-{Guid.NewGuid():N}");
        _fixtureSlnxPath = Path.Combine(_fixtureDir, "MinimalSolution.slnx");
    }

    public async Task InitializeAsync()
    {
        if (_skip)
        {
            return;
        }

        // Copy the fixture to a temp dir so the solution path (and derived pipe name) is unique.
        CopyFixtureToDir(_fixtureDir);

        // Ensure cold start.
        await StopDaemonAsync();
    }

    public async Task DisposeAsync()
    {
        if (_skip)
        {
            return;
        }

        await StopDaemonAsync();
        CleanupDirectory(_fixtureDir);
        CleanupDirectory(_stateDir);
    }

    [Fact]
    public async Task WhenTwoClientsQueryConcurrently_BothSucceedFromOneDaemon()
    {
        // Arrange
        if (_skip)
        {
            // Graceful skip when binary absent — consistent with ADR 0002.
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(DeadlineSeconds));

        // Warm up: run one command to ensure the daemon is loaded and ready.
        int warmupExit = await RunClientAsync(["list-projects", _fixtureSlnxPath], cts.Token);
        warmupExit.ShouldBe(0, "warm-up list-projects should succeed");

        // Act — start two client processes concurrently against the same running daemon.
        Task<int> client1 = RunClientAsync(["list-projects", _fixtureSlnxPath], cts.Token);
        Task<int> client2 = RunClientAsync(["list-projects", _fixtureSlnxPath], cts.Token);

        int[] results = await Task.WhenAll(client1, client2);

        // Assert — both clients succeed, proving concurrent serving.
        results[0].ShouldBe(0, "first concurrent client should exit 0");
        results[1].ShouldBe(0, "second concurrent client should exit 0");
    }

    private async Task<int> RunClientAsync(
        IReadOnlyList<string> commandArgs,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = _fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string arg in _prefixArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (string arg in commandArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.EnvironmentVariables["ROSLYN_QUERY_STATE_DIR"] = _stateDir;

        using Process client = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start roslyn-query client.");

        // Read stdout and stderr concurrently to prevent pipe-buffer deadlocks.
        Task<string> stdoutTask = client.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = client.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);
        await client.WaitForExitAsync(cancellationToken);
        return client.ExitCode;
    }

    private async Task StopDaemonAsync()
    {
        ProcessStartInfo stopInfo = new()
        {
            FileName = _fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string arg in _prefixArgs)
        {
            stopInfo.ArgumentList.Add(arg);
        }

        stopInfo.ArgumentList.Add("daemon");
        stopInfo.ArgumentList.Add("stop");
        stopInfo.ArgumentList.Add(_fixtureSlnxPath);
        stopInfo.EnvironmentVariables["ROSLYN_QUERY_STATE_DIR"] = _stateDir;

        using Process stop = Process.Start(stopInfo)
            ?? throw new InvalidOperationException("Failed to start roslyn-query to stop daemon.");

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(15));
        await stop.WaitForExitAsync(cts.Token);
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
