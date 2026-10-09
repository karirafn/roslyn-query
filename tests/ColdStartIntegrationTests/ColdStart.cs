using System.Diagnostics;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ColdStartIntegrationTests;

/// <summary>
/// Behavioral integration test: launches the real apphost cold, verifies that the caller's
/// piped stdout reaches EOF when the client exits while the daemon remains alive.
/// This test reproduces the hang described in issue #84 — against the unfixed
/// BuildStartInfo the read-to-EOF deadline will expire (test fails); after the fix it passes.
/// </summary>
public sealed class ColdStart : IAsyncLifetime
{
    private const int DeadlineSeconds = 30;

    private string _fixtureSlnxPath = string.Empty;

    public async Task InitializeAsync()
    {
        // Resolve the fixture solution that was copied to the test output directory.
        string fixtureDir = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "MinimalSolution");

        _fixtureSlnxPath = Path.GetFullPath(
            Path.Combine(fixtureDir, "MinimalSolution.slnx"));

        // Ensure a cold start: stop any running daemon for this fixture.
        await StopDaemonAsync(_fixtureSlnxPath);
    }

    public async Task DisposeAsync()
    {
        // Always stop the daemon after the test, even on failure.
        await StopDaemonAsync(_fixtureSlnxPath);
    }

    [Fact]
    public async Task WhenClientExitsWithPipedStdout_PipeReachesEofWhileDaemonStaysAlive()
    {
        // Arrange
        if (!AppHostLocator.TryLocate(out string fileName, out IReadOnlyList<string> prefixArgs))
        {
            // The binary was not found in the expected output path. This can happen when
            // the test assembly is run without a prior build of the src project.
            // In CI the binary is always built first — see the build workflow.
            return;
        }

        IReadOnlyList<string> commandArgs = [.. prefixArgs, "list-projects", _fixtureSlnxPath];

        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string arg in commandArgs)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(DeadlineSeconds));

        // Act — run the command and wait for the stdout pipe to reach EOF.
        using Process client = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the roslyn-query client process.");

        string stdout;
        try
        {
            // ReadToEndAsync reaches EOF only when all writers (client + any inherited daemon handle)
            // have closed the pipe. Under the unfixed code the daemon holds the caller's pipe handle,
            // so this blocks until the daemon exits (30-min idle). The deadline catches that hang.
            stdout = await client.StandardOutput.ReadToEndAsync(cts.Token);
            await client.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timed out — the pipe did not reach EOF within the deadline.
            // This is the exact hang the fix is meant to prevent.
            false.ShouldBeTrue(
                $"Stdout pipe did not reach EOF within {DeadlineSeconds}s. " +
                "The daemon may be holding the caller's pipe handle (issue #84).");
            return;
        }

        // Assert — the daemon must still be alive (EOF was caused by the client exiting,
        // not by the daemon dying).
        int? pid = DaemonProcess.ReadPidFile(_fixtureSlnxPath);
        pid.ShouldNotBeNull("Daemon PID file should exist after a successful cold-start command.");
        DaemonProcess.IsProcessRunning(pid.Value).ShouldBeTrue(
            "Daemon should remain alive after the client exits — " +
            "EOF on the stdout pipe must be caused by the client exiting, not by the daemon dying.");

        // The command output is not the focus — any non-crash exit from list-projects is fine.
        _ = stdout;
    }

    private static async Task StopDaemonAsync(string solutionPath)
    {
        if (!AppHostLocator.TryLocate(out string fileName, out IReadOnlyList<string> prefixArgs))
        {
            return;
        }

        IReadOnlyList<string> stopArgs = [.. prefixArgs, "daemon", "stop", solutionPath];

        ProcessStartInfo stopInfo = new()
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string arg in stopArgs)
        {
            stopInfo.ArgumentList.Add(arg);
        }

        using Process stop = Process.Start(stopInfo) ?? throw new InvalidOperationException(
            "Failed to start roslyn-query to stop daemon.");

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        await stop.WaitForExitAsync(cts.Token);
    }
}
