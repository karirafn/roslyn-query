using System.Diagnostics;

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

        // Spawn a short-lived process guaranteed to have a different name than "roslyn-query".
        // IsDaemonProcess compares process names — this process will not match.
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
            File.WriteAllText(
                pidFilePath,
                dummy.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // Act
            DaemonProcess.StopDaemon(_solutionPath);

            // Assert
            File.Exists(pidFilePath).ShouldBeTrue();
        }
        finally
        {
            dummy.Kill();
        }
    }

    public void Dispose()
    {
        DaemonProcess.CleanupPidFile(_solutionPath);
    }
}
