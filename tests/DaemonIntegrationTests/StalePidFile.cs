using System.Globalization;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonIntegrationTests;

public sealed class StalePidFile : IDisposable
{
    private readonly string _solutionPath;

    public StalePidFile()
    {
        _solutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
    }

    [Fact]
    public void WhenPidIsDead_IsProcessRunningReturnsFalse()
    {
        // Arrange
        int stalePid = int.MaxValue;

        // Act
        bool running = DaemonProcess.IsProcessRunning(stalePid);

        // Assert
        running.ShouldBeFalse();
    }

    [Fact]
    public void StopDaemon_CleansUpStalePidFile()
    {
        // Arrange
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        File.WriteAllText(pidFilePath, int.MaxValue.ToString(CultureInfo.InvariantCulture));
        File.Exists(pidFilePath).ShouldBeTrue();

        // Act
        DaemonProcess.StopDaemon(_solutionPath);

        // Assert
        File.Exists(pidFilePath).ShouldBeFalse();
    }

    [Fact]
    public void StopDaemon_WithStalePid_DoesNotThrow()
    {
        // Arrange
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        File.WriteAllText(pidFilePath, int.MaxValue.ToString(CultureInfo.InvariantCulture));

        // Act & Assert
        Should.NotThrow(() => DaemonProcess.StopDaemon(_solutionPath));
    }

    [Fact]
    public void ReadPidFile_ReturnsStalePid()
    {
        // Arrange
        int stalePid = int.MaxValue;
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        File.WriteAllText(pidFilePath, stalePid.ToString(CultureInfo.InvariantCulture));

        // Act
        int? readPid = DaemonProcess.ReadPidFile(_solutionPath);

        // Assert
        readPid.ShouldBe(stalePid);
    }

    public void Dispose()
    {
        DaemonProcess.CleanupPidFile(_solutionPath);
    }
}
