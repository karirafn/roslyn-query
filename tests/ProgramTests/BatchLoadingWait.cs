using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ProgramTests;

/// <summary>
/// Tests for the batch loading-wait behavior in DaemonCommandRunner:
/// - perpetual-loading batch exits 75 + no-commands message + zero headers
/// - line-2 error still prints line-1 and line-3 under headers
/// </summary>
public sealed class BatchLoadingWait
{
    private static readonly string FakeSolutionPath =
        Path.Combine(Path.GetTempPath(), "batch-loading-test.sln");

    [Fact]
    public async Task WhenDaemonPerpetuallyLoading_Exits75WithNoCommandsMessageAndNoHeaders()
    {
        // Arrange — the loading-wait probe always returns Loading; clock past 90s
        StringWriter stdout = new();
        StringWriter stderr = new();
        string[] lines = ["find-refs Foo", "find-refs Bar"];

        DaemonCommandRunner.TryExecuteHandler tryExecute =
            (_, _, _, _, ct) =>
                Task.FromResult(new DaemonAttempt(DaemonOutcome.Loading, 0));

        // Act
        int result = await DaemonCommandRunner.RunBatchAsync(
            FakeSolutionPath,
            lines,
            [],
            stdout,
            stderr,
            tryExecute,
            spawn: _ => null,
            kill: _ => { },
            runDirect: (_, _) => Task.FromResult(5),
            clock: () => TimeSpan.FromSeconds(91));

        // Assert
        result.ShouldBe(ExitCodes.Transient);
        string stdoutStr = stdout.ToString();
        stdoutStr.ShouldNotContain("===");  // No command headers emitted
        string stderrStr = stderr.ToString();
        stderrStr.ShouldContain("daemon still loading; no commands run");
        stderrStr.ShouldEndWith("\n");
    }

    [Fact]
    public async Task WhenLine2ErrorsOrTimesOut_RemainingLinesStillRunUnderHeaders()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        string[] lines = ["find-refs Foo", "find-refs Bar", "find-refs Baz"];

        // Call sequence: first call is the loading-wait probe (returns Result → ready),
        // then one call per line.
        int callCount = 0;

        DaemonAttempt[] lineResults =
        [
            new(DaemonOutcome.Result, 0),   // line 1 succeeds
            new(DaemonOutcome.Result, 1),   // line 2 "fails" (error exit)
            new(DaemonOutcome.Result, 0),   // line 3 succeeds
        ];

        DaemonCommandRunner.TryExecuteHandler tryExecute =
            (_, _, _, _, ct) =>
            {
                if (callCount == 0)
                {
                    // Loading-wait probe
                    callCount++;
                    return Task.FromResult(new DaemonAttempt(DaemonOutcome.Result, 0));
                }

                int lineIndex = callCount - 1;
                callCount++;
                DaemonAttempt a = lineResults[Math.Min(lineIndex, lineResults.Length - 1)];
                return Task.FromResult(a);
            };

        // Act
        int result = await DaemonCommandRunner.RunBatchAsync(
            FakeSolutionPath,
            lines,
            [],
            stdout,
            stderr,
            tryExecute,
            spawn: _ => null,
            kill: _ => { },
            runDirect: (_, _) => Task.FromResult(5),
            clock: () => TimeSpan.Zero);

        // Assert — all lines execute; last exit code is the last line's (0)
        result.ShouldBe(0);
        string stdoutStr = stdout.ToString();
        stdoutStr.ShouldContain("=== find-refs Foo ===");
        stdoutStr.ShouldContain("=== find-refs Bar ===");
        stdoutStr.ShouldContain("=== find-refs Baz ===");
    }
}
