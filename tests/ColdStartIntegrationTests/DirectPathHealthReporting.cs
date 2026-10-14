using System.Diagnostics;

using Shouldly;

namespace roslyn_query.Tests.ColdStartIntegrationTests;

/// <summary>
/// Integration test for the direct-path health reporting introduced in step 9 of issue #86.
/// Verifies that when the roslyn-query binary runs list-projects against the MinimalSolution
/// fixture it emits "workspace: complete" on stderr regardless of whether the request is
/// served by the daemon or via the direct (RunDirect) fallback path.
/// </summary>
public sealed class DirectPathHealthReporting : IAsyncLifetime
{
    private const int DeadlineSeconds = 60;

    private string _fixtureSlnxPath = string.Empty;

    public async Task InitializeAsync()
    {
        string fixtureDir = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "MinimalSolution");

        _fixtureSlnxPath = Path.GetFullPath(
            Path.Combine(fixtureDir, "MinimalSolution.slnx"));

        // Ensure a cold start so the test owns the daemon lifecycle.
        await StopDaemonAsync(_fixtureSlnxPath);
    }

    public async Task DisposeAsync()
    {
        await StopDaemonAsync(_fixtureSlnxPath);
    }

    [Fact]
    public async Task WhenListProjectsRunsAgainstCleanFixture_StderrContainsWorkspaceComplete()
    {
        // Arrange
        if (!AppHostLocator.TryLocate(out string fileName, out IReadOnlyList<string> prefixArgs))
        {
            // Binary not built — skip gracefully (consistent with ColdStart.cs approach).
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

        // Act
        using Process client = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the roslyn-query client process.");

        string stderr;
        string stdout;
        try
        {
            Task<string> readStdout = client.StandardOutput.ReadToEndAsync(cts.Token);
            Task<string> readStderr = client.StandardError.ReadToEndAsync(cts.Token);
            await Task.WhenAll(readStdout, readStderr);
            stdout = await readStdout;
            stderr = await readStderr;
            await client.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new ShouldAssertException(
                $"Process did not complete within {DeadlineSeconds}s. " +
                "The workspace load may have hung or the daemon did not respond.");
        }

        // Assert
        client.ExitCode.ShouldBe(0, "list-projects should succeed against the MinimalSolution fixture.");
        stderr.ShouldContain("workspace: complete");

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
