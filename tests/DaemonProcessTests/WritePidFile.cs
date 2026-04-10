using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonProcessTests;

public sealed class WritePidFile : IDisposable
{
    private readonly string _solutionPath;

    public WritePidFile()
    {
        _solutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
    }

    [Fact]
    public void WritesCurrentProcessId()
    {
        // Arrange & Act
        DaemonProcess.WritePidFile(_solutionPath);

        // Assert
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        string content = File.ReadAllText(pidFilePath);
        int pid = int.Parse(content, System.Globalization.CultureInfo.InvariantCulture);
        pid.ShouldBe(Environment.ProcessId);
    }

    [Fact]
    public void ReadPidFile_ReturnsWrittenPid()
    {
        // Arrange
        DaemonProcess.WritePidFile(_solutionPath);

        // Act
        int? pid = DaemonProcess.ReadPidFile(_solutionPath);

        // Assert
        pid.ShouldBe(Environment.ProcessId);
    }

    [Fact]
    public void ReadPidFile_WhenFileDoesNotExist_ReturnsNull()
    {
        // Arrange
        string nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");

        // Act
        int? pid = DaemonProcess.ReadPidFile(nonExistentPath);

        // Assert
        pid.ShouldBeNull();
    }

    [Fact]
    public void ReadPidFile_WhenContentIsInvalid_ReturnsNull()
    {
        // Arrange
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        File.WriteAllText(pidFilePath, "not-a-number");

        // Act
        int? pid = DaemonProcess.ReadPidFile(_solutionPath);

        // Assert
        pid.ShouldBeNull();
    }

    public void Dispose()
    {
        DaemonProcess.CleanupPidFile(_solutionPath);
    }
}
