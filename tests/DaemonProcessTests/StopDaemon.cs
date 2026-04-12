using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonProcessTests;

public sealed class StopDaemon : IDisposable
{
    private readonly string _solutionPath;

    public StopDaemon()
    {
        _solutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
    }

    [Fact]
    public void WhenPidFileContainsBogusPid_DoesNotThrow()
    {
        // Arrange
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        File.WriteAllText(pidFilePath, int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Act & Assert
        Should.NotThrow(() => DaemonProcess.StopDaemon(_solutionPath));
    }

    [Fact]
    public void WhenPidFileContainsBogusPid_CleansUpPidFile()
    {
        // Arrange
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        File.WriteAllText(pidFilePath, int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Act
        DaemonProcess.StopDaemon(_solutionPath);

        // Assert
        File.Exists(pidFilePath).ShouldBeFalse();
    }

    [Fact]
    public void WhenNoPidFile_DoesNotThrow()
    {
        // Act & Assert
        Should.NotThrow(() => DaemonProcess.StopDaemon(_solutionPath));
    }

    [Fact]
    public void WhenPidBelongsToNonDaemonProcess_DoesNotDeletePidFile()
    {
        // Arrange
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        int explorerPid = System.Diagnostics.Process.GetProcessesByName("explorer")[0].Id;
        File.WriteAllText(
            pidFilePath,
            explorerPid.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Act
        DaemonProcess.StopDaemon(_solutionPath);

        // Assert
        File.Exists(pidFilePath).ShouldBeTrue();
    }

    public void Dispose()
    {
        DaemonProcess.CleanupPidFile(_solutionPath);
    }
}
