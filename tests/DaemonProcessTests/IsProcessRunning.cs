using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonProcessTests;

public sealed class IsProcessRunning
{
    [Fact]
    public void CurrentProcess_ReturnsTrue()
    {
        // Arrange
        int pid = Environment.ProcessId;

        // Act
        bool result = DaemonProcess.IsProcessRunning(pid);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void BogusPid_ReturnsFalse()
    {
        // Arrange
        int pid = int.MaxValue;

        // Act
        bool result = DaemonProcess.IsProcessRunning(pid);

        // Assert
        result.ShouldBeFalse();
    }
}
