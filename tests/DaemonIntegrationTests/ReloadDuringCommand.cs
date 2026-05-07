using System.IO.Pipes;

using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonIntegrationTests;

public sealed class ReloadDuringCommand
{
    private const int TransientExitCode = 75;

    [Fact]
    public async Task CommandDuringReload_CompletesWithTransientResponse()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);

        AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        ReloadState reloadState = new(solution, []);

        reloadState.TryBeginReload().ShouldBeTrue();

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        Task serverTask = Task.Run(
            async () =>
            {
                using NamedPipeServerStream pipe = new(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cts.Token);
                await PipeProtocol.ReadRequestAsync(pipe, cts.Token);

                await PipeProtocol.WriteResponseAsync(
                    pipe,
                    "",
                    "daemon: workspace reloading",
                    TransientExitCode);
            },
            cts.Token);

        StringWriter stdout = new();
        StringWriter stderr = new();

        // Act
        (int? exitCode, bool wasReloading) = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["find-callers", "--symbol", "Foo.Bar"],
            stdout,
            stderr);

        await serverTask;

        // Assert
        exitCode.ShouldBeNull();
        wasReloading.ShouldBeTrue();
        stderr.ToString().ShouldBe("daemon: workspace reloading");

        reloadState.CompleteReload(solution, []);

        reloadState.Solution.ShouldNotBeNull();
    }

    [Fact]
    public async Task WhenDaemonReturnsExitCode75_ReturnsNullAndSetsWasReloading()
    {
        // Arrange
        string fakeSolutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        string pipeName = PipeProtocol.DerivePipeName(fakeSolutionPath);

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(10));

        Task serverTask = Task.Run(
            async () =>
            {
                using NamedPipeServerStream pipe = new(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(cts.Token);
                await PipeProtocol.ReadRequestAsync(pipe, cts.Token);

                await PipeProtocol.WriteResponseAsync(
                    pipe,
                    "",
                    "daemon: workspace reloading",
                    TransientExitCode);
            },
            cts.Token);

        StringWriter stdout = new();
        StringWriter stderr = new();

        // Act
        (int? exitCode, bool wasReloading) = await DaemonClient.TryExecuteAsync(
            fakeSolutionPath,
            ["find-callers", "--symbol", "Foo.Bar"],
            stdout,
            stderr);

        await serverTask;

        // Assert
        exitCode.ShouldBeNull();
        wasReloading.ShouldBeTrue();
        stderr.ToString().ShouldBe("daemon: workspace reloading");
    }
}
