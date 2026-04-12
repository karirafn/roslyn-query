using System.Runtime.InteropServices;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonServerTests;

public sealed class ApplyUnixPipeUmask
{
#pragma warning disable CA5392 // P/Invoke targets libc — DefaultDllImportSearchPath does not apply
    [DllImport("libc", SetLastError = false)]
    private static extern uint umask(uint mask);
#pragma warning restore CA5392

    [Fact]
    public void OnNonWindows_NewFileHasOwnerOnlyPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        // Act
        uint previous = DaemonServer.ApplyUnixPipeUmask();

        try
        {
            File.WriteAllText(tempFile, "test");

            // Assert
            UnixFileMode mode = File.GetUnixFileMode(tempFile);
            mode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            _ = umask(previous);
            File.Delete(tempFile);
        }
    }
}
