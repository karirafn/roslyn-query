using System.Runtime.CompilerServices;

using RoslynQuery;

namespace roslyn_query.Tests;

internal static class TestHostInitialization
{
    // An in-process daemon's background-load failure path calls Environment.Exit(1) to fail
    // closed in production. Inside the test host that would kill the xUnit process and abort
    // the run, so engage the process-wide suppress latch once before any test runs.
    // The per-test AsyncLocal seam (SetSuppressEnvironmentExitForTests) covers only the calling
    // async context; a detached Task.Run that outlives the test body escapes that flow and can
    // still reach the exit call after the test's Dispose resets the flag. The process-wide latch
    // is a one-way write that covers the entire process lifetime regardless of task scheduling.
    [ModuleInitializer]
    internal static void SuppressDaemonProcessExit() =>
        DaemonServer.SuppressEnvironmentExitProcessWideForTests();
}
