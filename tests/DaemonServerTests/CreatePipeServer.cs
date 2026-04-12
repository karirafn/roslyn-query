using System.IO.Pipes;

using Shouldly;

namespace roslyn_query.Tests.DaemonServerTests;

public sealed class CreatePipeServer
{
    [Fact]
    public void OnUnix_SetsPipeSocketToOwnerReadWriteOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        string pipeName = "test-acl-" + Guid.NewGuid().ToString("N");
        string socketPath = Path.Combine(Path.GetTempPath(), $"CoreFxPipe_{pipeName}");

        // Act
        using NamedPipeServerStream pipe = new(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        File.SetUnixFileMode(
            socketPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // Assert
        UnixFileMode mode = File.GetUnixFileMode(socketPath);
        mode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
