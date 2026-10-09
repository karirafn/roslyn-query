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
        // Arrange — point state directory at a unique path guaranteed not to exist;
        // the AsyncLocal override provides full isolation without touching the filesystem.
        string absentDir = Path.Combine(Path.GetTempPath(), $"rq-absent-{Guid.NewGuid():N}");
        PipeProtocol.SetStateDirectoryOverrideForTests(absentDir);

        // Act & Assert — must not throw
        Should.NotThrow(() => DaemonProcess.StopAllDaemons());

        // Assert — StopAllDaemons must not create the directory
        Directory.Exists(absentDir).ShouldBeFalse();
    }

    public void Dispose()
    {
        PipeProtocol.SetStateDirectoryOverrideForTests(null);

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
