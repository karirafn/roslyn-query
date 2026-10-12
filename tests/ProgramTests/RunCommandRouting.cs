using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ProgramTests;

/// <summary>
/// Tests for DaemonCommandRunner.RunAsync — verifies that each DaemonOutcome routes
/// to the correct action via injectable seams (no real processes).
/// </summary>
public sealed class RunCommandRouting
{
    private static readonly string FakeSolutionPath =
        Path.Combine(Path.GetTempPath(), "routing-test.sln");

    private static readonly string[] Args = ["find-refs", "Foo"];

    [Fact]
    public async Task WhenOutcomeIsResult_ReturnsExitCode()
    {
        // Arrange
        DaemonAttempt attempt = new(DaemonOutcome.Result, 42);
        int callCount = 0;

        DaemonCommandRunner.TryExecuteHandler tryExecute =
            (_, _, _, _, ct) =>
            {
                callCount++;
                return Task.FromResult(attempt);
            };

        // Act
        int result = await DaemonCommandRunner.RunAsync(
            FakeSolutionPath,
            Args,
            Console.Out,
            Console.Error,
            tryExecute,
            spawn: _ => { },
            kill: _ => { },
            runDirect: (_, _) => Task.FromResult(5),
            clock: () => TimeSpan.Zero);

        // Assert
        result.ShouldBe(42);
        callCount.ShouldBe(1);
    }

    [Fact]
    public async Task WhenOutcomeIsResultWithError_ReturnsExitCode1_NoRetry()
    {
        // Arrange — Error is surfaced as Result exit 1, not retried
        DaemonAttempt attempt = new(DaemonOutcome.Result, 1);
        int callCount = 0;

        DaemonCommandRunner.TryExecuteHandler tryExecute =
            (_, _, _, _, ct) =>
            {
                callCount++;
                return Task.FromResult(attempt);
            };

        // Act
        int result = await DaemonCommandRunner.RunAsync(
            FakeSolutionPath,
            Args,
            Console.Out,
            Console.Error,
            tryExecute,
            spawn: _ => { },
            kill: _ => { },
            runDirect: (_, _) => Task.FromResult(5),
            clock: () => TimeSpan.Zero);

        // Assert — no retry for error-as-result
        result.ShouldBe(1);
        callCount.ShouldBe(1);
    }

    [Fact]
    public async Task WhenOutcomeIsResponseTimeout_Returns75WithTrailingNewline()
    {
        // Arrange
        DaemonAttempt timeoutAttempt = new(DaemonOutcome.ResponseTimeout, 0);
        StringWriter stderr = new();

        DaemonCommandRunner.TryExecuteHandler tryExecute =
            (_, _, _, _, ct) => Task.FromResult(timeoutAttempt);

        // Act
        int result = await DaemonCommandRunner.RunAsync(
            FakeSolutionPath,
            Args,
            Console.Out,
            stderr,
            tryExecute,
            spawn: _ => { },
            kill: _ => { },
            runDirect: (_, _) => Task.FromResult(5),
            clock: () => TimeSpan.Zero);

        // Assert
        result.ShouldBe(ExitCodes.Transient);
        string stderrOutput = stderr.ToString();
        stderrOutput.ShouldContain("daemon did not respond; retry");
        stderrOutput.ShouldEndWith("\n");
    }

    [Fact]
    public async Task WhenOutcomeIsNotRunning_SpawnsAndRetries()
    {
        // Arrange — first attempt NotRunning, second returns Result
        int callCount = 0;
        bool spawned = false;

        DaemonAttempt[] sequence =
        [
            new(DaemonOutcome.NotRunning, 0),
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
            spawn: _ => { spawned = true; },
            kill: _ => { },
            runDirect: (_, _) => Task.FromResult(5),
            clock: () => TimeSpan.Zero,
            isDaemonAliveAfterSpawn: _ => true);

        // Assert
        result.ShouldBe(0);
        spawned.ShouldBeTrue();
        callCount.ShouldBe(2);
    }

    [Fact]
    public async Task WhenMalformedOrShort_KillsAndRespawns()
    {
        // Arrange — MalformedOrShort → kill+respawn → Result
        int callCount = 0;
        bool killed = false;
        bool spawned = false;

        DaemonAttempt[] sequence =
        [
            new(DaemonOutcome.MalformedOrShort, 0),
            new(DaemonOutcome.Result, 7),
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
            spawn: _ => { spawned = true; },
            kill: _ => { killed = true; },
            runDirect: (_, _) => Task.FromResult(5),
            clock: () => TimeSpan.Zero,
            isDaemonAliveAfterSpawn: _ => true);

        // Assert
        result.ShouldBe(7);
        killed.ShouldBeTrue();
        spawned.ShouldBeTrue();
        callCount.ShouldBe(2);
    }

    [Fact]
    public async Task WhenWedged_KillsAndRespawns()
    {
        // Arrange
        int callCount = 0;
        bool killed = false;
        bool spawned = false;

        DaemonAttempt[] sequence =
        [
            new(DaemonOutcome.Wedged, 0),
            new(DaemonOutcome.Result, 3),
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
            spawn: _ => { spawned = true; },
            kill: _ => { killed = true; },
            runDirect: (_, _) => Task.FromResult(5),
            clock: () => TimeSpan.Zero,
            isDaemonAliveAfterSpawn: _ => true);

        // Assert
        result.ShouldBe(3);
        killed.ShouldBeTrue();
        spawned.ShouldBeTrue();
        callCount.ShouldBe(2);
    }

    [Fact]
    public async Task WhenSpawnedButDaemonDead_RunsDirect()
    {
        // Arrange — NotRunning → spawn → isDaemonAliveAfterSpawn returns false → RunDirect
        bool directRan = false;
        int callCount = 0;

        DaemonCommandRunner.TryExecuteHandler tryExecute =
            (_, _, _, _, ct) =>
            {
                callCount++;
                return Task.FromResult(new DaemonAttempt(DaemonOutcome.NotRunning, 0));
            };

        // Act
        int result = await DaemonCommandRunner.RunAsync(
            FakeSolutionPath,
            Args,
            Console.Out,
            Console.Error,
            tryExecute,
            spawn: _ => { },
            kill: _ => { },
            runDirect: (_, _) => { directRan = true; return Task.FromResult(99); },
            clock: () => TimeSpan.Zero,
            isDaemonAliveAfterSpawn: _ => false);

        // Assert
        directRan.ShouldBeTrue();
        result.ShouldBe(99);
        // Only one attempt made (before spawn), then RunDirect
        callCount.ShouldBe(1);
    }
}
