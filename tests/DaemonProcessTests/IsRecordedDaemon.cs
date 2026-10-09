using System.Diagnostics;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonProcessTests;

public sealed class IsRecordedDaemon
{
    [Fact]
    public void WhenStartTimeMatches_ReturnsTrue()
    {
        // Arrange
        Process current = Process.GetCurrentProcess();
        long ticks = current.StartTime.ToUniversalTime().Ticks;

        // Act
        bool result = DaemonProcess.IsRecordedDaemon(current, ticks);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void WhenStartTimeDiffers_ReturnsFalse()
    {
        // Arrange
        ProcessStartInfo startInfo = new()
        {
            FileName = OperatingSystem.IsWindows() ? "ping" : "sleep",
            Arguments = OperatingSystem.IsWindows() ? "-n 30 127.0.0.1" : "30",
            CreateNoWindow = true,
            UseShellExecute = false,
        };

        using Process dummy = Process.Start(startInfo)!;
        long wrongTicks = dummy.StartTime.ToUniversalTime().Ticks + 1;

        try
        {
            // Act
            bool result = DaemonProcess.IsRecordedDaemon(dummy, wrongTicks);

            // Assert
            result.ShouldBeFalse();
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
}
