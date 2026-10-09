using System.Diagnostics;
using System.Globalization;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonProcessTests;

public sealed class StartDaemon : IDisposable
{
    private readonly string _solutionPath;
    private readonly string _stateDir;

    public StartDaemon()
    {
        _stateDir = Path.Combine(Path.GetTempPath(), $"rq-test-startdaemon-{Guid.NewGuid():N}");
        PipeProtocol.SetStateDirectoryOverrideForTests(_stateDir);
        _solutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
    }

    [Fact]
    public void WhenNoPidFile_InvokesSpawn()
    {
        // Arrange
        bool spawnCalled = false;
        void Spawn() => spawnCalled = true;

        // Act
        DaemonProcess.StartDaemon(_solutionPath, Spawn);

        // Assert
        spawnCalled.ShouldBeTrue();
    }

    [Fact]
    public void WhenPidFileExistsAndProcessIsAliveDaemon_DoesNotInvokeSpawn()
    {
        // Arrange — write two-line record for current process so IsRecordedDaemon recognises it
        DaemonProcess.WritePidFile(_solutionPath);

        bool spawnCalled = false;
        void Spawn() => spawnCalled = true;

        // Act
        DaemonProcess.StartDaemon(_solutionPath, Spawn);

        // Assert
        spawnCalled.ShouldBeFalse();
    }

    [Fact]
    public void WhenPidFileExistsAndProcessIsAliveDaemon_PidFileRemainsIntact()
    {
        // Arrange — write two-line record for current process so IsRecordedDaemon recognises it
        DaemonProcess.WritePidFile(_solutionPath);
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);

        // Act
        DaemonProcess.StartDaemon(_solutionPath, () => { });

        // Assert
        File.Exists(pidFilePath).ShouldBeTrue();
    }

    [Fact]
    public void WhenPidFileExistsButProcessIsDead_CleansPidFileAndInvokesSpawn()
    {
        // Arrange — write a PID that is guaranteed not to be alive
        Directory.CreateDirectory(_stateDir);
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        File.WriteAllText(
            pidFilePath,
            int.MaxValue.ToString(CultureInfo.InvariantCulture));

        bool spawnCalled = false;
        void Spawn() => spawnCalled = true;

        // Act
        DaemonProcess.StartDaemon(_solutionPath, Spawn);

        // Assert
        spawnCalled.ShouldBeTrue();
        File.Exists(pidFilePath).ShouldBeFalse();
    }

    [Fact]
    public void WhenPidFileExistsButPidBelongsToNonDaemonProcess_CleansPidFileAndInvokesSpawn()
    {
        // Arrange — spawn a real child process, write its PID with a deliberately wrong start-time
        // ticks so IsRecordedDaemon returns false, exercising the "alive but wrong time" path.
        ProcessStartInfo startInfo = new()
        {
            FileName = OperatingSystem.IsWindows() ? "ping" : "sleep",
            Arguments = OperatingSystem.IsWindows() ? "-n 30 127.0.0.1" : "30",
            CreateNoWindow = true,
            UseShellExecute = false,
        };

        using Process dummy = Process.Start(startInfo)!;
        Directory.CreateDirectory(_stateDir);
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        long wrongTicks = dummy.StartTime.ToUniversalTime().Ticks + 1;

        File.WriteAllText(
            pidFilePath,
            dummy.Id.ToString(CultureInfo.InvariantCulture)
            + "\n"
            + wrongTicks.ToString(CultureInfo.InvariantCulture)
            + "\n");

        bool spawnCalled = false;
        void Spawn() => spawnCalled = true;

        try
        {
            // Act
            DaemonProcess.StartDaemon(_solutionPath, Spawn);

            // Assert
            spawnCalled.ShouldBeTrue();
            File.Exists(pidFilePath).ShouldBeFalse();
        }
        finally
        {
#pragma warning disable CA1031 // Swallowing all exceptions in test cleanup — process may already be dead
            try
            {
                dummy.Kill();
                dummy.WaitForExit();
            }
            catch (Exception)
            {
                // Process may already be dead — ignore
            }
#pragma warning restore CA1031
        }
    }

    [Fact]
    public void WhenPidFileHasLegacySingleLineForAliveProcess_CleansAndSpawns()
    {
        // Arrange — write just the current process PID (no start-time line).
        // The process is genuinely alive, but the legacy format means no start time can be
        // verified — fail closed: spawn is still invoked and the file is cleaned.
        Directory.CreateDirectory(_stateDir);
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        File.WriteAllText(
            pidFilePath,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        bool spawnCalled = false;
        void Spawn() => spawnCalled = true;

        // Act
        DaemonProcess.StartDaemon(_solutionPath, Spawn);

        // Assert
        spawnCalled.ShouldBeTrue();
        File.Exists(pidFilePath).ShouldBeFalse();
    }

    public void Dispose()
    {
        PipeProtocol.SetStateDirectoryOverrideForTests(null);
        DaemonProcess.CleanupPidFile(_solutionPath);

        if (Directory.Exists(_stateDir))
        {
            foreach (string file in Directory.EnumerateFiles(_stateDir))
            {
                File.Delete(file);
            }

            Directory.Delete(_stateDir);
        }
    }
}
