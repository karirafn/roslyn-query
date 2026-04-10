using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonProcessTests;

public sealed class CleanupPidFile
{
    [Fact]
    public void WhenFileExists_DeletesFile()
    {
        // Arrange
        string solutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
        DaemonProcess.WritePidFile(solutionPath);
        string pidFilePath = PipeProtocol.DerivePidFilePath(solutionPath);
        File.Exists(pidFilePath).ShouldBeTrue();

        // Act
        DaemonProcess.CleanupPidFile(solutionPath);

        // Assert
        File.Exists(pidFilePath).ShouldBeFalse();
    }

    [Fact]
    public void WhenFileDoesNotExist_DoesNotThrow()
    {
        // Arrange
        string solutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");

        // Act & Assert
        Should.NotThrow(() => DaemonProcess.CleanupPidFile(solutionPath));
    }
}
