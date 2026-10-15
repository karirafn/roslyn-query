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

    // On Unix, DerivePipeName returns a rooted socket path under a per-user directory that must
    // exist before a NamedPipeServerStream can bind to it. In-process fake-server tests derive
    // the path without creating the directory, so create the default once for the test host.
    // Tests that set their own socket-dir override (via SetSocketDirectoryOverrideForTests or
    // ROSLYN_QUERY_SOCKET_DIR) create their own directory; this covers the no-override path only.
    [ModuleInitializer]
    internal static void EnsureSocketDirectoryExists()
    {
#pragma warning disable CA1031 // Best-effort initializer: swallowing all exceptions is intentional — each test that needs a specific dir creates it
        try
        {
            Directory.CreateDirectory(PipeProtocol.GetSocketDirectory());
        }
        catch (Exception)
        {
            // Swallowing is intentional — this is a pre-test host initializer, not production code.
            // A test that sets its own socket-dir override (via SetSocketDirectoryOverrideForTests
            // or ROSLYN_QUERY_SOCKET_DIR) creates its own directory; this covers the no-override path only.
        }
#pragma warning restore CA1031
    }
}
