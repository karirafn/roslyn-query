using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ProgramTests;

/// <summary>
/// Tests for the loading-wait behavior in DaemonCommandRunner:
/// - loading-then-ready returns the eventual result
/// - loading past 90s returns 75 with exact message + trailing newline
/// </summary>
public sealed class LoadingWait
{
    private static readonly string FakeSolutionPath =
        Path.Combine(Path.GetTempPath(), "loading-wait-test.sln");

    private static readonly string[] Args = ["find-refs", "Foo"];

    [Fact]
    public async Task WhenLoadingThenReady_ReturnsEventualResult()
    {
        // Arrange — Loading × 3, then Result
        int callCount = 0;

        DaemonAttempt[] sequence =
        [
            new(DaemonOutcome.Loading, 0),
            new(DaemonOutcome.Loading, 0),
            new(DaemonOutcome.Loading, 0),
            new(DaemonOutcome.Result, 0),
        ];

        DaemonCommandRunner.TryExecuteHandler tryExecute =
            (_, _, _, _, ct) =>
            {
                DaemonAttempt a = sequence[Math.Min(callCount, sequence.Length - 1)];
                callCount++;
                return Task.FromResult(a);
            };

        // Act
        int result = await DaemonCommandRunner.RunAsync(
            FakeSolutionPath,
            Args,
            Console.Out,
            Console.Error,
            tryExecute,
            spawn: _ => null,
            kill: _ => { },
            runDirect: (_, _) => Task.FromResult(5),
            // Return a fixed small elapsed time — stays well below the 90s loading deadline
            clock: () => TimeSpan.FromSeconds(1));

        // Assert
        result.ShouldBe(0);
        callCount.ShouldBeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public async Task WhenLoadingExceeds90s_Returns75WithExactMessageAndTrailingNewline()
    {
        // Arrange — always Loading; clock immediately returns past deadline
        int callCount = 0;
        StringWriter stderr = new();

        DaemonCommandRunner.TryExecuteHandler tryExecute =
            (_, _, _, _, ct) =>
            {
                callCount++;
                return Task.FromResult(new DaemonAttempt(DaemonOutcome.Loading, 0));
            };

        // Act
        int result = await DaemonCommandRunner.RunAsync(
            FakeSolutionPath,
            Args,
            Console.Out,
            stderr,
            tryExecute,
            spawn: _ => null,
            kill: _ => { },
            runDirect: (_, _) => Task.FromResult(5),
            clock: () => TimeSpan.FromSeconds(91));  // Immediately past 90s deadline

        // Assert
        result.ShouldBe(ExitCodes.Transient);
        string stderrOutput = stderr.ToString();
        stderrOutput.ShouldContain("daemon still loading; retry - load continues in background");
        stderrOutput.ShouldEndWith("\n");
    }

    [Fact]
    public async Task WhenLoadingBeforeDeadline_EmitsProgressMessages()
    {
        // Arrange — loading for a few cycles then times out at 90s
        int callCount = 0;
        StringWriter stderr = new();
        int clockCalls = 0;

        // Set injectable cadence so progress is emitted every 5s (fake)
        DaemonCommandRunner.SetLoadingProgressCadenceForTests(TimeSpan.FromSeconds(5));

        try
        {
            DaemonCommandRunner.TryExecuteHandler tryExecute =
                (_, _, _, _, ct) =>
                {
                    callCount++;
                    return Task.FromResult(new DaemonAttempt(DaemonOutcome.Loading, 0));
                };

            // Act — clock advances 5s per call; hits 90s deadline after 18 iterations
            int result = await DaemonCommandRunner.RunAsync(
                FakeSolutionPath,
                Args,
                Console.Out,
                stderr,
                tryExecute,
                spawn: _ => null,
                kill: _ => { },
                runDirect: (_, _) => Task.FromResult(5),
                clock: () =>
                {
                    clockCalls++;
                    return TimeSpan.FromSeconds(clockCalls * 5);
                });

            // Assert
            result.ShouldBe(ExitCodes.Transient);
            string stderrOutput = stderr.ToString();
            stderrOutput.ShouldContain("daemon: loading workspace");
            stderrOutput.ShouldContain("daemon still loading; retry - load continues in background");
        }
        finally
        {
            DaemonCommandRunner.SetLoadingProgressCadenceForTests(null);
        }
    }
}
