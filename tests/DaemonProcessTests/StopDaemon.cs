using System.Diagnostics;
using System.IO.Pipes;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonProcessTests;

public sealed class StopDaemon : IDisposable
{
    private readonly string _stateDir;
    private readonly string _solutionPath;

    public StopDaemon()
    {
        _stateDir = Path.Combine(Path.GetTempPath(), $"rq-stopdaemon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_stateDir);
        PipeProtocol.SetStateDirectoryOverrideForTests(_stateDir);
        _solutionPath = Path.Combine(_stateDir, Guid.NewGuid() + ".sln");
    }

    [Fact]
    public async Task WhenNoPidFile_DoesNotThrow()
    {
        // Act & Assert
        await Should.NotThrowAsync(() => DaemonProcess.StopDaemon(_solutionPath));
    }

    [Fact]
    public async Task WhenPidFileContainsBogusPid_DoesNotThrow()
    {
        // Arrange — two-line record format: pid + start-time ticks; int.MaxValue is guaranteed dead
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        string content =
            int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\n"
            + DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\n";
        await File.WriteAllTextAsync(pidFilePath, content);

        // Act & Assert — dead PID → ArgumentException → treated as stale → no throw
        await Should.NotThrowAsync(() => DaemonProcess.StopDaemon(_solutionPath));
    }

    [Fact]
    public async Task WhenPidFileContainsBogusPid_CleansUpPidFile()
    {
        // Arrange — two-line record format: pid + start-time ticks
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        string content =
            int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\n"
            + DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\n";
        await File.WriteAllTextAsync(pidFilePath, content);

        // Act — pipe not reachable (no server listening); dead PID → ArgumentException → stale → deleted
        await DaemonProcess.StopDaemon(_solutionPath);

        // Assert
        File.Exists(pidFilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task WhenPidBelongsToNonDaemonProcess_DoesNotDeletePidFile()
    {
        // Arrange — spawn a live non-daemon process; write its PID with a deliberately wrong
        // start-time so IsRecordedDaemon returns false → not killed, file left intact.
        ProcessStartInfo startInfo = new()
        {
            FileName = OperatingSystem.IsWindows() ? "ping" : "sleep",
            Arguments = OperatingSystem.IsWindows() ? "-n 30 127.0.0.1" : "30",
            CreateNoWindow = true,
            UseShellExecute = false,
        };

        using Process dummy = Process.Start(startInfo)!;
        try
        {
            string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);

            // Wrong start-time ticks — IsRecordedDaemon will return false
            long wrongTicks = dummy.StartTime.ToUniversalTime().Ticks - 1_000_000L;
            string content =
                dummy.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "\n"
                + wrongTicks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "\n";
            await File.WriteAllTextAsync(pidFilePath, content);

            // Act — pipe unreachable (no server), start-time mismatch → not killed, file intact
            await DaemonProcess.StopDaemon(_solutionPath);

            // Assert
            File.Exists(pidFilePath).ShouldBeTrue();
        }
        finally
        {
            dummy.Kill();
        }
    }

    [Fact]
    public async Task WhenDaemonReachableOverPipe_RequestsShutdownAndCleansPidFile()
    {
        // Arrange — stand up a minimal in-process daemon server on the pipe derived from our solution path
        string pipeName = PipeProtocol.DerivePipeName(_solutionPath);
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);

        // Write a two-line PID record for the current process so the fallback kill path
        // would recognise it as "our" process — but the pipe-first path should be taken.
        using Process selfProcess = Process.GetCurrentProcess();
        string pidContent =
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\n"
            + selfProcess.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\n";
        await File.WriteAllTextAsync(pidFilePath, pidContent);

        using CancellationTokenSource serverCts = new(TimeSpan.FromSeconds(15));

        // Minimal in-process server: handle one shutdown request then exit (mirrors Shutdown.cs)
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
                            // Simulate daemon's finally block: remove PID file and exit loop
                            if (File.Exists(pidFilePath))
                            {
                                File.Delete(pidFilePath);
                            }

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
        await DaemonProcess.StopDaemon(_solutionPath);

        // Assert — server loop exited (acked shutdown) and PID file is gone
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

        serverExited.ShouldBeTrue();
        File.Exists(pidFilePath).ShouldBeFalse();
    }

    public void Dispose()
    {
        DaemonProcess.CleanupPidFile(_solutionPath);
        PipeProtocol.SetStateDirectoryOverrideForTests(null);
    }
}
