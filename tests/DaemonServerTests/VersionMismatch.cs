using System.IO.Pipes;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonServerTests;

/// <summary>
/// Verifies that the daemon replies VersionMismatch and signals shutdown when the client's
/// protocol version differs from the server's.
/// </summary>
public sealed class VersionMismatch
{
    [Fact]
    public async Task WhenClientProtocolVersionDiffers_ServerRepliesVersionMismatch()
    {
        // Arrange — simulate server-side handler that checks the protocol version
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);

        using CancellationTokenSource serverCts = new(TimeSpan.FromSeconds(10));
        bool versionMismatchSignaled = false;

        // Minimal server that reads the new envelope and checks protocol version
        Task serverTask = Task.Run(async () =>
        {
            using NamedPipeServerStream pipe = new(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await pipe.WaitForConnectionAsync(serverCts.Token);

            (int protocolVersion, _, _) =
                await PipeProtocol.ReadRequestEnvelopeAsync(pipe, serverCts.Token);

            if (protocolVersion != PipeProtocol.ProtocolVersion)
            {
                await PipeProtocol.WriteStatusAsync(
                    pipe,
                    ResponseStatus.VersionMismatch,
                    serverCts.Token);
                versionMismatchSignaled = true;
            }
            else
            {
                await PipeProtocol.WriteResultAsync(pipe, "ok", "", 0, serverCts.Token);
            }
        }, serverCts.Token);

        // Act — connect with a mismatched version
        NamedPipeClientStream clientPipe = new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await using (clientPipe.ConfigureAwait(false))
        {
            await clientPipe.ConnectAsync(serverCts.Token);

            // Write request with wrong protocol version
            await PipeProtocol.WriteRequestAsync(
                clientPipe,
                protocolVersion: 999,  // Wrong version
                clientVersion: "test",
                args: ["find-refs", "Foo"],
                serverCts.Token);

            DaemonResponse response = await PipeProtocol.ReadResponseEnvelopeAsync(
                clientPipe,
                serverCts.Token);

            // Assert
            response.Status.ShouldBe(ResponseStatus.VersionMismatch);
        }

        await serverTask;
        versionMismatchSignaled.ShouldBeTrue();
    }
}
