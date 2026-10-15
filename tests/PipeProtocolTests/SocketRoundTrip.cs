using System.IO.Pipes;
using System.Text;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.PipeProtocolTests;

/// <summary>
/// Proves that a rooted Unix socket path returned by DerivePipeName binds via
/// NamedPipeServerStream and accepts a NamedPipeClientStream connection — i.e. that
/// GetPipePath's rooted-path branch is actually exercised.
/// </summary>
public sealed class SocketRoundTrip : IDisposable
{
    private readonly string _socketDir;

    public SocketRoundTrip()
    {
        _socketDir = Path.Combine(Path.GetTempPath(), $"rq-roundtrip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_socketDir);
        PipeProtocol.SetSocketDirectoryOverrideForTests(_socketDir);
    }

    public void Dispose()
    {
        PipeProtocol.SetSocketDirectoryOverrideForTests(null);

        if (Directory.Exists(_socketDir))
        {
            foreach (string file in Directory.EnumerateFiles(_socketDir))
            {
                File.Delete(file);
            }

            Directory.Delete(_socketDir);
        }
    }

    [Fact]
    public async Task OnUnix_RootedPipeName_BindsAndAcceptsConnection()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        const string SolutionPath = "/tmp/RoundTrip.sln";
        const string Message = "hello-round-trip";
        string pipeName = PipeProtocol.DerivePipeName(SolutionPath);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));
        CancellationToken cancellationToken = cts.Token;

        // Act — run server and client concurrently
        Task serverTask = RunServerAsync(pipeName, Message, cancellationToken);
        Task<string> clientTask = RunClientAsync(pipeName, cancellationToken);

        await Task.WhenAll(serverTask, clientTask);
        string received = await clientTask;

        // Assert
        received.ShouldBe(Message);
    }

    private static async Task RunServerAsync(
        string pipeName,
        string message,
        CancellationToken cancellationToken)
    {
        // Pass "." as server name and the rooted path as pipe name — GetPipePath uses it verbatim.
        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.Out,
            maxNumberOfServerInstances: 1,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await server.WaitForConnectionAsync(cancellationToken);

        byte[] payload = Encoding.UTF8.GetBytes(message);
        await server.WriteAsync(payload, cancellationToken);
        await server.FlushAsync(cancellationToken);
    }

    private static async Task<string> RunClientAsync(
        string pipeName,
        CancellationToken cancellationToken)
    {
        // Small retry loop: the server may not be listening yet when the client connects.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                // "." as server name is the convention for local named pipes / Unix domain sockets.
                NamedPipeClientStream client = new(
                    serverName: ".",
                    pipeName: pipeName,
                    direction: PipeDirection.In,
                    options: PipeOptions.Asynchronous);

                await using (client)
                {
                    await client.ConnectAsync(cancellationToken);
                    using StreamReader reader = new(client, Encoding.UTF8, leaveOpen: true);
                    return await reader.ReadToEndAsync(cancellationToken);
                }
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(50, cancellationToken);
            }
        }

        throw new InvalidOperationException("Client failed to connect after 20 attempts.");
    }
}
