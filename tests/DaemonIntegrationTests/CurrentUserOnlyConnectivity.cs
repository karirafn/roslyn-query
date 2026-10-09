using System.IO.Pipes;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonIntegrationTests;

/// <summary>
/// Security coverage for the Windows pipe-spoofing fix (ADR 0001).
///
/// The meaningful rejection — a client refusing a server pipe owned by a different user —
/// is Windows pipe-owner semantics and is not reproducible on Linux CI. The test below
/// covers only the cross-platform claim: same-user connections still succeed when the
/// production ClientPipeOptions constant includes CurrentUserOnly.
///
/// Manual verification steps (Windows only):
///   Positive  — start the daemon from a non-elevated shell and run a query from the
///               same shell; the fast path (daemon reuse) is used, no fallback.
///   Negative  — have a second local user pre-create a named pipe at the derived name;
///               run a query as the first user; DaemonClient.TryExecuteAsync catches the
///               UnauthorizedAccessException, returns (null, false), and the CLI falls
///               back to a direct in-process run — no spoofed output is printed.
///   Cross-elevation (accepted limitation) — start the daemon elevated and run a query
///               non-elevated (or vice-versa); the fast path is refused, the CLI falls
///               back to a direct run, and the correct result is still produced (slower).
/// </summary>
public sealed class CurrentUserOnlyConnectivity
{
    // The round-trip still succeeds when the client connects to a same-user server pipe.
    // This proves CurrentUserOnly does not break legitimate same-user connections on any
    // platform — on Linux the runtime compares effective uids (same process, same uid,
    // passes); on Windows it compares owner SIDs (both sides key off .Owner, passes).
    [Fact]
    public async Task WhenSameUserServerIsRunning_ClientRoundTripSucceeds()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);
        string[] sentArgs = ["find-refs", "MyType"];
        string expectedStdout = "find-refs MyType";

        // The server pipe does NOT use CurrentUserOnly — this mirrors the inline server
        // pattern used throughout RoundTrip.cs and tests that the client's CurrentUserOnly
        // flag does not reject a same-user server regardless of the server's flags.
        Task serverTask = Task.Run(async () =>
        {
            using NamedPipeServerStream pipe = new(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await pipe.WaitForConnectionAsync();
            string[] args = await PipeProtocol.ReadRequestAsync(pipe, CancellationToken.None);
            await PipeProtocol.WriteResponseAsync(
                pipe,
                string.Join(" ", args),
                "",
                0);
        });

        StringWriter stdout = new();
        StringWriter stderr = new();

        // Act
        (int? exitCode, bool wasReloading) = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            sentArgs,
            stdout,
            stderr);

        await serverTask;

        // Assert
        exitCode.ShouldBe(0);
        wasReloading.ShouldBeFalse();
        stdout.ToString().ShouldBe(expectedStdout);
        stderr.ToString().ShouldBe("");
    }
}
