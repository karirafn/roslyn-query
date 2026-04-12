using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonServerTests;

public sealed class ApplyUnixPipeUmask
{
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
        DaemonServer.ApplyUnixPipeUmask();
        File.WriteAllText(tempFile, "test");

        try
        {
            // Assert
            UnixFileMode mode = File.GetUnixFileMode(tempFile);
            mode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
