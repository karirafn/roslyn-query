using System.IO.Pipes;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonIntegrationTests;

public sealed class Shutdown
{
    [Fact]
    public async Task WhenNoDaemonRunning_TryRequestShutdownAsync_ReturnsFalse()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");

        // Act
        bool result = await DaemonClient.TryRequestShutdownAsync(fakeSolutionPath);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task WhenDaemonRunning_TryRequestShutdownAsync_ReturnsTrueAndDaemonLoopExits()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);

        using CancellationTokenSource serverCts = new(TimeSpan.FromSeconds(10));

        // Minimal in-process daemon that mirrors DaemonServer's accept loop
        Task serverTask = Task.Run(
            async () =>
            {
                while (!serverCts.Token.IsCancellationRequested)
                {
                    try
                    {
#pragma warning disable CA2000 // Disposed by await using
                        await using NamedPipeServerStream pipe = new(
                            pipeName,
                            PipeDirection.InOut,
                            1,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);
#pragma warning restore CA2000

                        await pipe.WaitForConnectionAsync(serverCts.Token);

                        string[] args = await PipeProtocol.ReadRequestAsync(pipe, serverCts.Token);

                        if (args is [PipeProtocol.ShutdownCommand])
                        {
                            await PipeProtocol.WriteResponseAsync(pipe, "", "", 0, serverCts.Token);
                            pipe.Disconnect();
                            break;
                        }

                        await PipeProtocol.WriteResponseAsync(
                            pipe,
                            string.Join(" ", args),
                            "",
                            0,
                            serverCts.Token);

                        pipe.Disconnect();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            },
            serverCts.Token);

        // Act
        bool result = await DaemonClient.TryRequestShutdownAsync(fakeSolutionPath);

        // Give the server loop up to 5 seconds to exit after responding
        using CancellationTokenSource exitWaitCts = new(TimeSpan.FromSeconds(5));
        bool serverExited;
        try
        {
            await serverTask.WaitAsync(exitWaitCts.Token);
            serverExited = true;
        }
        catch (OperationCanceledException)
        {
            serverExited = false;
            await serverCts.CancelAsync();
        }

        // Assert
        result.ShouldBeTrue();
        serverExited.ShouldBeTrue();
    }

    [Fact]
    public async Task WhenNormalArgsReceived_DaemonDoesNotShutDown()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);
        int requestsHandled = 0;

        using CancellationTokenSource serverCts = new(TimeSpan.FromSeconds(10));

        // Minimal in-process daemon with shutdown interception — normal args must pass through
        Task serverTask = Task.Run(
            async () =>
            {
                while (!serverCts.Token.IsCancellationRequested)
                {
                    try
                    {
#pragma warning disable CA2000 // Disposed by await using
                        await using NamedPipeServerStream pipe = new(
                            pipeName,
                            PipeDirection.InOut,
                            1,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous);
#pragma warning restore CA2000

                        await pipe.WaitForConnectionAsync(serverCts.Token);

                        string[] args = await PipeProtocol.ReadRequestAsync(pipe, serverCts.Token);

                        if (args is [PipeProtocol.ShutdownCommand])
                        {
                            await PipeProtocol.WriteResponseAsync(pipe, "", "", 0, serverCts.Token);
                            pipe.Disconnect();
                            break;
                        }

                        await PipeProtocol.WriteResponseAsync(
                            pipe,
                            string.Join(" ", args),
                            "",
                            0,
                            serverCts.Token);

                        pipe.Disconnect();
                        requestsHandled++;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            },
            serverCts.Token);

        StringWriter stdout = new();
        StringWriter stderr = new();

        // Act — send a normal command (not shutdown)
        (int? exitCode, bool wasReloading) = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["find-refs", "MyType"],
            stdout,
            stderr);

        // Assert — normal dispatch succeeded; daemon is still running
        exitCode.ShouldBe(0);
        wasReloading.ShouldBeFalse();
        stdout.ToString().ShouldBe("find-refs MyType");
        requestsHandled.ShouldBe(1);

        // Cleanup — shut down the daemon
        await serverCts.CancelAsync();
        try
        {
            await serverTask;
        }
        catch (OperationCanceledException)
        {
            // Expected on CTS cancellation
        }
    }
}
