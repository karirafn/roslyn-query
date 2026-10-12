using System.Diagnostics;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ProgramTests;

/// <summary>
/// Tests the race-free cold-start liveness path in DaemonCommandRunner:
/// when spawn returns an alive Process the runner must wait for the pipe (retrying
/// NotRunning → Loading → Result) rather than immediately calling RunDirect.
/// When the spawned process is confirmed dead, RunDirect IS called.
/// Covers both RunAsync and WaitForReadyAsync (batch) paths.
/// </summary>
public sealed class ColdStartLiveness
{
    private static readonly string FakeSolutionPath =
        Path.Combine(Path.GetTempPath(), "cold-start-liveness-test.sln");

    private static readonly string[] Args = ["find-refs", "Foo"];

    [Fact]
    public async Task WhenSpawnedProcessAliveAndPipeComesUp_NeverCallsRunDirect_RunAsync()
    {
        // Arrange — spawn returns an alive process; pipe comes up after two NotRunning polls
        int callCount = 0;
        int runDirectCalls = 0;

        DaemonAttempt[] sequence =
        [
            new(DaemonOutcome.NotRunning, 0),  // initial probe — daemon not yet running
            new(DaemonOutcome.NotRunning, 0),  // first retry after spawn — pipe still coming up
            new(DaemonOutcome.Loading, 0),     // pipe open, daemon loading
            new(DaemonOutcome.Result, 42),     // load complete — result
        ];

        DaemonCommandRunner.TryExecuteHandler tryExecute =
            (_, _, _, _, ct) =>
            {
                DaemonAttempt a = sequence[Math.Min(callCount, sequence.Length - 1)];
                callCount++;
                return Task.FromResult(a);
            };

        // Use a long-running real process as the "spawned daemon" so HasExited is false.
        // The sleep duration is long enough that the process outlives this test.
        using Process aliveProcess = CreateAliveProcess();

        // Act
        int result = await DaemonCommandRunner.RunAsync(
            FakeSolutionPath,
            Args,
            Console.Out,
            Console.Error,
            tryExecute,
            spawn: _ => aliveProcess,
            kill: _ => { },
            runDirect: (_, _) => { runDirectCalls++; return Task.FromResult(99); },
            clock: () => TimeSpan.FromSeconds(1));

        // Assert
        result.ShouldBe(42, "should return the daemon result, not RunDirect's sentinel");
        runDirectCalls.ShouldBe(0, "RunDirect must not be called when the spawned process is alive");
        callCount.ShouldBe(4, "should have polled until Result");

        aliveProcess.Kill();
    }

    [Fact]
    public async Task WhenSpawnedProcessDead_CallsRunDirect_RunAsync()
    {
        // Arrange — spawn returns a process that has already exited
        int runDirectCalls = 0;
        int callCount = 0;

        DaemonCommandRunner.TryExecuteHandler tryExecute =
            (_, _, _, _, ct) =>
            {
                callCount++;
                return Task.FromResult(new DaemonAttempt(DaemonOutcome.NotRunning, 0));
            };

        using Process deadProcess = CreateDeadProcess();

        // Act
        int result = await DaemonCommandRunner.RunAsync(
            FakeSolutionPath,
            Args,
            Console.Out,
            Console.Error,
            tryExecute,
            spawn: _ => deadProcess,
            kill: _ => { },
            runDirect: (_, _) => { runDirectCalls++; return Task.FromResult(88); },
            clock: () => TimeSpan.Zero);

        // Assert
        runDirectCalls.ShouldBe(1, "RunDirect must be called when the spawned process is dead");
        result.ShouldBe(88, "should return RunDirect's result");
    }

    [Fact]
    public async Task WhenSpawnedProcessAliveAndPipeComesUp_NeverCallsRunDirect_WaitForReady()
    {
        // Arrange — batch path: spawn returns an alive process; pipe comes up after two polls
        int callCount = 0;
        int runDirectCalls = 0;

        // WaitForReadyAsync uses a sentinel probe; sequence:
        // call 0: initial NotRunning → spawn
        // call 1: NotRunning → pipe still coming up (spawned process alive, no RunDirect)
        // call 2: Result → ready
        // then the batch line itself:
        // call 3: Result for the actual batch command
        DaemonAttempt[] sequence =
        [
            new(DaemonOutcome.NotRunning, 0),  // initial probe
            new(DaemonOutcome.NotRunning, 0),  // pipe still coming up
            new(DaemonOutcome.Result, 0),      // ready
            new(DaemonOutcome.Result, 7),      // batch line result
        ];

        DaemonCommandRunner.TryExecuteHandler tryExecute =
            (_, _, _, _, ct) =>
            {
                DaemonAttempt a = sequence[Math.Min(callCount, sequence.Length - 1)];
                callCount++;
                return Task.FromResult(a);
            };

        using Process aliveProcess = CreateAliveProcess();

        StringWriter stdout = new();

        // Act
        int result = await DaemonCommandRunner.RunBatchAsync(
            FakeSolutionPath,
            ["find-refs Foo"],
            [],
            stdout,
            Console.Error,
            tryExecute,
            spawn: _ => aliveProcess,
            kill: _ => { },
            runDirect: (_, _) => { runDirectCalls++; return Task.FromResult(99); },
            clock: () => TimeSpan.FromSeconds(1));

        // Assert
        runDirectCalls.ShouldBe(0, "RunDirect must not be called when the spawned process is alive");
        result.ShouldBe(7, "should return the batch line result");
        string stdoutStr = stdout.ToString();
        stdoutStr.ShouldContain("=== find-refs Foo ===");

        aliveProcess.Kill();
    }

    [Fact]
    public async Task WhenSpawnedProcessDeadInBatch_BatchExitsTransient()
    {
        // Arrange — batch path: spawn returns a dead process → WaitForReadyAsync returns false
        int runDirectCalls = 0;

        DaemonCommandRunner.TryExecuteHandler tryExecute =
            (_, _, _, _, ct) =>
                Task.FromResult(new DaemonAttempt(DaemonOutcome.NotRunning, 0));

        using Process deadProcess = CreateDeadProcess();

        StringWriter stdout = new();
        StringWriter stderr = new();

        // Act
        int result = await DaemonCommandRunner.RunBatchAsync(
            FakeSolutionPath,
            ["find-refs Foo"],
            [],
            stdout,
            stderr,
            tryExecute,
            spawn: _ => deadProcess,
            kill: _ => { },
            runDirect: (_, _) => { runDirectCalls++; return Task.FromResult(99); },
            clock: () => TimeSpan.Zero);

        // Assert
        runDirectCalls.ShouldBe(0, "RunDirect should not be called in the batch path (batch exits transient)");
        result.ShouldBe(ExitCodes.Transient, "batch exits transient when daemon cannot come up");
        stdout.ToString().ShouldBeEmpty("no command headers when daemon dead");
        stderr.ToString().ShouldContain("daemon still loading; no commands run");
    }

    /// <summary>Creates a process that is alive (sleeping) for the duration of the test.</summary>
    private static Process CreateAliveProcess()
    {
        // Use a long sleep so the process outlives this test.
        ProcessStartInfo startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c timeout /t 30 /nobreak > nul")
            : new ProcessStartInfo("sleep", "30");

        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardInput = false;
        startInfo.RedirectStandardOutput = false;
        startInfo.RedirectStandardError = false;

        Process process = new() { StartInfo = startInfo };
        process.Start();
        return process;
    }

    /// <summary>Creates a process that exits immediately, giving a dead process for liveness checks.</summary>
    private static Process CreateDeadProcess()
    {
        ProcessStartInfo startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0")
            : new ProcessStartInfo("true");

        startInfo.UseShellExecute = false;

        Process process = new() { StartInfo = startInfo };
        process.Start();
        process.WaitForExit();
        return process;
    }
}
