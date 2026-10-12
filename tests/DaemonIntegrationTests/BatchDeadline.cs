using System.Diagnostics;

using Shouldly;

using AppHostLocator = roslyn_query.Tests.ColdStartIntegrationTests.AppHostLocator;

namespace roslyn_query.Tests.DaemonIntegrationTests;

/// <summary>
/// Real-apphost integration test: a batch run against a perpetually-loading daemon exits
/// with code 75 (EX_TEMPFAIL) and emits no command headers to stdout.
///
/// The daemon is held in the Loading state by ROSLYN_QUERY_TEST_LOAD_DELAY_MS and the
/// client's loading deadline is shortened via ROSLYN_QUERY_TEST_LOADING_DEADLINE_MS so
/// the deadline expires before the daemon becomes ready — proving the batch runner
/// aborts before writing any "=== command ===" header lines.
/// </summary>
public sealed class BatchDeadline : IAsyncLifetime
{
    private const int TestDeadlineSeconds = 60;
    private const int ClientDeadlineMs = 500;
    private const int DaemonLoadDelayMs = 30_000;
    private const int DaemonStartTimeoutMs = 10_000;

    private readonly string _fixtureSlnxPath;
    private readonly string _fixtureDir;
    private readonly string _stateDir;
    private readonly string _batchFilePath;
    private readonly string _fileName;
    private readonly IReadOnlyList<string> _prefixArgs;
    private readonly bool _skip;

    public BatchDeadline()
    {
        if (!AppHostLocator.TryLocate(out string fileName, out IReadOnlyList<string> prefixArgs))
        {
            _skip = true;
            _fileName = string.Empty;
            _prefixArgs = [];
            _fixtureSlnxPath = string.Empty;
            _fixtureDir = string.Empty;
            _stateDir = string.Empty;
            _batchFilePath = string.Empty;
            return;
        }

        _fileName = fileName;
        _prefixArgs = prefixArgs;

        _fixtureDir = Path.Combine(Path.GetTempPath(), $"rq-batchdeadline-fix-{Guid.NewGuid():N}");
        _stateDir = Path.Combine(Path.GetTempPath(), $"rq-batchdeadline-state-{Guid.NewGuid():N}");
        _fixtureSlnxPath = Path.Combine(_fixtureDir, "MinimalSolution.slnx");

        // A batch file with one command — should never execute because the deadline fires first.
        _batchFilePath = Path.Combine(Path.GetTempPath(), $"rq-batch-{Guid.NewGuid():N}.txt");
    }

    public async Task InitializeAsync()
    {
        if (_skip)
        {
            return;
        }

        CopyFixtureToDir(_fixtureDir);
        Directory.CreateDirectory(_stateDir);

        // Write the batch command file: one list-projects command.
        await File.WriteAllTextAsync(
            _batchFilePath,
            $"list-projects {_fixtureSlnxPath}{Environment.NewLine}");

        await StopDaemonAsync();
        await StartDaemonWithLoadDelayAsync();
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

        if (File.Exists(_batchFilePath))
        {
            File.Delete(_batchFilePath);
        }
    }

    [Fact]
    public async Task WhenBatchRunAgainstLoadingDaemon_ExitsTransientWithNoHeaders()
    {
        // Arrange
        if (_skip)
        {
            // Graceful skip when binary absent — consistent with ADR 0002.
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(TestDeadlineSeconds));

        ProcessStartInfo startInfo = BuildClientStartInfo(
            ["batch", _fixtureSlnxPath, _batchFilePath]);

        // Shorten the client-side loading deadline so the deadline fires before the daemon
        // finishes loading.
        startInfo.EnvironmentVariables["ROSLYN_QUERY_TEST_ENABLE_SEAMS"] = "1";
        startInfo.EnvironmentVariables["ROSLYN_QUERY_TEST_LOADING_DEADLINE_MS"] =
            ClientDeadlineMs.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Act
        using Process client = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start roslyn-query client.");

        string[] outputs = await Task.WhenAll(
            client.StandardError.ReadToEndAsync(cts.Token),
            client.StandardOutput.ReadToEndAsync(cts.Token));
        string stderr = outputs[0];
        string stdout = outputs[1];
        await client.WaitForExitAsync(cts.Token);

        // Assert — batch must exit 75 when the loading deadline expires.
        client.ExitCode.ShouldBe(
            75,
            $"expected transient exit (75) when batch loading deadline expires; stderr: {stderr}");

        // No command headers should be written — the batch runner aborts before the loop.
        stdout.ShouldBeEmpty(
            $"expected empty stdout (no headers emitted before deadline); stdout: {stdout}");

        // The "no commands run" message must appear on stderr.
        stderr.ShouldContain(
            "daemon still loading",
            Case.Insensitive,
            $"expected deadline message; full stderr: {stderr}");
    }

    private async Task StartDaemonWithLoadDelayAsync()
    {
        ProcessStartInfo daemonInfo = new()
        {
            FileName = _fileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string arg in _prefixArgs)
        {
            daemonInfo.ArgumentList.Add(arg);
        }

        daemonInfo.ArgumentList.Add("--daemon");
        daemonInfo.ArgumentList.Add(_fixtureSlnxPath);

        daemonInfo.EnvironmentVariables["ROSLYN_QUERY_TEST_ENABLE_SEAMS"] = "1";
        daemonInfo.EnvironmentVariables["ROSLYN_QUERY_TEST_LOAD_DELAY_MS"] =
            DaemonLoadDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        daemonInfo.EnvironmentVariables["ROSLYN_QUERY_STATE_DIR"] = _stateDir;

        using Process daemon = Process.Start(daemonInfo)
            ?? throw new InvalidOperationException("Failed to start roslyn-query daemon.");

        daemon.StandardInput.Close();
        daemon.StandardOutput.BaseStream.Close();
        daemon.StandardError.BaseStream.Close();

        string pidFilePath = DaemonPidFilePath(_stateDir, _fixtureSlnxPath);
        using CancellationTokenSource cts = new(DaemonStartTimeoutMs);

        while (!File.Exists(pidFilePath))
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, cts.Token);
        }
    }

    private ProcessStartInfo BuildClientStartInfo(IReadOnlyList<string> commandArgs)
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
        return startInfo;
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

    /// <summary>
    /// Returns the PID file path for the given solution, mirroring PipeProtocol.DerivePidFilePath.
    /// Naming: <c>{stateDir}/roslyn-query-{SHA256(normalizedPath)[..32]}.pid</c>.
    /// </summary>
    private static string DaemonPidFilePath(string stateDir, string slnxPath)
    {
        string fullPath = System.IO.Path.TrimEndingDirectorySeparator(Path.GetFullPath(slnxPath));
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fullPath));
        string hexUpper = Convert.ToHexString(hash);
        char[] hexLower = new char[hexUpper.Length];
        for (int i = 0; i < hexUpper.Length; i++)
        {
            hexLower[i] = hexUpper[i] is >= 'A' and <= 'F'
                ? (char)(hexUpper[i] + 32)
                : hexUpper[i];
        }

        string stem = "roslyn-query-" + new string(hexLower, 0, 32);
        return Path.Combine(stateDir, stem + ".pid");
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
