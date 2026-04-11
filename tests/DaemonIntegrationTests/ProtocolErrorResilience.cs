using System.Buffers.Binary;
using System.IO.Pipes;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonIntegrationTests;

public sealed class ProtocolErrorResilience
{
    [Fact]
    public async Task DaemonSurvivesMaliciousClient_ThenServesValidClient()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);
        int connectionsHandled = 0;

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        // Simulate a daemon loop that reads requests and handles protocol errors
        Task serverTask = Task.Run(
            async () =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        using NamedPipeServerStream pipe = new(
                            pipeName,
                            PipeDirection.InOut,
                            1,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);

                        await pipe.WaitForConnectionAsync(cts.Token);

                        string[] args = await PipeProtocol.ReadRequestAsync(
                            pipe,
                            cts.Token);

                        await PipeProtocol.WriteResponseAsync(
                            pipe,
                            string.Join(" ", args),
                            "",
                            0);

                        connectionsHandled++;
                        pipe.Disconnect();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (InvalidDataException) when (!cts.Token.IsCancellationRequested)
                    {
                        // Same pattern as DaemonServer — survive protocol errors
                    }
                    catch (IOException) when (!cts.Token.IsCancellationRequested)
                    {
                        // Broken pipe — survive and continue
                    }
                }
            },
            cts.Token);

        // Act — malicious client sends a negative frame length
        using (NamedPipeClientStream badClient = new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous))
        {
            await badClient.ConnectAsync(cts.Token);
            byte[] badLenBytes = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(badLenBytes, -1);
            await badClient.WriteAsync(badLenBytes, cts.Token);
            await badClient.FlushAsync(cts.Token);
        }

        // Brief wait for the server to process the error and loop back
        await Task.Delay(100, cts.Token);

        // Act — valid client connects after the malicious one
        StringWriter stdout = new();
        StringWriter stderr = new();
        int? exitCode = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["find-callers", "--symbol", "Foo.Bar"],
            stdout,
            stderr);

        await cts.CancelAsync();

        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Assert
        exitCode.ShouldBe(0);
        stdout.ToString().ShouldBe("find-callers --symbol Foo.Bar");
        connectionsHandled.ShouldBe(1);
    }
}
