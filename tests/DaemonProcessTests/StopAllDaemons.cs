using System.Diagnostics;
using System.Globalization;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonProcessTests;

public sealed class StopAllDaemons : IDisposable
{
    private readonly List<string> _pidFilePaths = [];

    [Fact]
    public void WhenStalePidFilesExist_DeletesThem()
    {
        // Arrange
        string pidFilePath = CreatePidFile(int.MaxValue);

        // Act
        DaemonProcess.StopAllDaemons();

        // Assert
        File.Exists(pidFilePath).ShouldBeFalse();
    }

    [Fact]
    public void WhenPidFileContainsNonIntegerContent_DeletesFile()
    {
        // Arrange
        string pidFilePath = CreatePidFileWithContent("not-a-number");

        // Act
        DaemonProcess.StopAllDaemons();

        // Assert
        File.Exists(pidFilePath).ShouldBeFalse();
    }

    [Fact]
    public void WhenPidBelongsToNonDaemonProcess_LeavesPidFileIntact()
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
        string pidFilePath = CreatePidFile(dummy.Id);

        try
        {
            // Act
            DaemonProcess.StopAllDaemons();

            // Assert
            File.Exists(pidFilePath).ShouldBeTrue();
        }
        finally
        {
            dummy.Kill();
        }
    }

    [Fact]
    public void WhenStateDirectoryAbsent_DoesNotThrow()
    {
        // Arrange
        // The state directory may already exist on this machine, so we cannot fully assert
        // "does not create it" without the step-3 env-var override. This test asserts at
        // minimum that StopAllDaemons() does not throw when the early-return path is taken.
        // The strict "does not create missing dir" assertion is deferred to step 3.
        //
        // We verify the behaviour by temporarily renaming the directory if it exists,
        // calling StopAllDaemons, then restoring it.
        string stateDir = PipeProtocol.GetStateDirectory();
        string? renamedDir = null;

        if (Directory.Exists(stateDir))
        {
            renamedDir = stateDir + ".bak-test-" + Guid.NewGuid().ToString("N");
            Directory.Move(stateDir, renamedDir);
        }

        try
        {
            // Act & Assert — must not throw
            Should.NotThrow(() => DaemonProcess.StopAllDaemons());

            // Assert — directory was not created by StopAllDaemons
            Directory.Exists(stateDir).ShouldBeFalse();
        }
        finally
        {
            if (renamedDir is not null && Directory.Exists(renamedDir))
            {
                Directory.Move(renamedDir, stateDir);
            }
        }
    }

    public void Dispose()
    {
        foreach (string path in _pidFilePaths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static string PidDirectory =>
        Path.GetDirectoryName(PipeProtocol.DerivePidFilePath("any.sln"))!;

    private string CreatePidFile(int pid)
    {
        // Create a PID file with the roslyn-query-*.pid naming pattern
        string fileName = $"roslyn-query-{Guid.NewGuid():N}.pid";
        string pidFilePath = Path.Combine(PidDirectory, fileName);
        File.WriteAllText(pidFilePath, pid.ToString(CultureInfo.InvariantCulture));
        _pidFilePaths.Add(pidFilePath);
        return pidFilePath;
    }

    private string CreatePidFileWithContent(string content)
    {
        string fileName = $"roslyn-query-{Guid.NewGuid():N}.pid";
        string pidFilePath = Path.Combine(PidDirectory, fileName);
        File.WriteAllText(pidFilePath, content);
        _pidFilePaths.Add(pidFilePath);
        return pidFilePath;
    }
}
