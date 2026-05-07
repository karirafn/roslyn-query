using System.Diagnostics;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonProcessTests;

public sealed class StartDaemon : IDisposable
{
    private readonly string _solutionPath;

    public StartDaemon()
    {
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
        // Arrange
        // Write current process PID — same executable name as IsDaemonProcess expects
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        File.WriteAllText(
            pidFilePath,
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

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
        // Arrange
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        File.WriteAllText(
            pidFilePath,
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Act
        DaemonProcess.StartDaemon(_solutionPath, () => { });

        // Assert
        File.Exists(pidFilePath).ShouldBeTrue();
    }

    [Fact]
    public void WhenPidFileExistsButProcessIsDead_CleansPidFileAndInvokesSpawn()
    {
        // Arrange
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        File.WriteAllText(
            pidFilePath,
            int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));

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
        // Arrange
        // Spawn a process with a different executable name — IsDaemonProcess will return false
        ProcessStartInfo startInfo = new()
        {
            FileName = OperatingSystem.IsWindows() ? "ping" : "sleep",
            Arguments = OperatingSystem.IsWindows() ? "-n 30 127.0.0.1" : "30",
            CreateNoWindow = true,
            UseShellExecute = false,
        };

        using Process dummy = Process.Start(startInfo)!;
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        File.WriteAllText(
            pidFilePath,
            dummy.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

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

    public void Dispose()
    {
        DaemonProcess.CleanupPidFile(_solutionPath);
    }
}
