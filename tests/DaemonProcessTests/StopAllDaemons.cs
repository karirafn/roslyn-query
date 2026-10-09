using System.Diagnostics;
using System.Globalization;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonProcessTests;

public sealed class StopAllDaemons : IDisposable
{
    private readonly string _stateDir;
    private readonly List<string> _pidFilePaths = [];

    public StopAllDaemons()
    {
        _stateDir = Path.Combine(Path.GetTempPath(), $"rq-stopalldaemons-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_stateDir);
        PipeProtocol.SetStateDirectoryOverrideForTests(_stateDir);
    }

    [Fact]
    public void WhenStalePidFilesExist_DeletesThem()
    {
        // Arrange — two-line record with a dead PID (int.MaxValue never alive)
        string pidFilePath = CreatePidFile(int.MaxValue, DateTime.UtcNow.Ticks);

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
        // Arrange — spawn a live process; write its PID with a deliberately wrong start-time
        // so IsRecordedDaemon returns false → not killed, file left intact (fail closed).
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
            long wrongTicks = dummy.StartTime.ToUniversalTime().Ticks - 1_000_000L;
            string pidFilePath = CreatePidFile(dummy.Id, wrongTicks);

            // Act
            DaemonProcess.StopAllDaemons();

            // Assert — start-time mismatch: not our daemon → leave intact
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
        // Arrange — point state directory at a unique path guaranteed not to exist
        string absentDir = Path.Combine(Path.GetTempPath(), $"rq-absent-{Guid.NewGuid():N}");
        PipeProtocol.SetStateDirectoryOverrideForTests(absentDir);
        try
        {
            // Act & Assert — must not throw
            Should.NotThrow(() => DaemonProcess.StopAllDaemons());

            // Assert — StopAllDaemons must not create the directory
            Directory.Exists(absentDir).ShouldBeFalse();
        }
        finally
        {
            PipeProtocol.SetStateDirectoryOverrideForTests(_stateDir);
        }
    }

    [Fact]
    public void WhenPidFileIsLegacySingleLineForNonDeadProcess_LeavesIntact()
    {
        // Arrange — legacy single-line file (no start-time): a live non-dead process.
        // Without a start-time we cannot verify identity → fail closed → leave intact.
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
            // Single-line legacy file: pid only, no start-time line
            string pidFilePath = CreatePidFileWithPidOnly(dummy.Id);

            // Act
            DaemonProcess.StopAllDaemons();

            // Assert — cannot verify identity → file left intact
            File.Exists(pidFilePath).ShouldBeTrue();
        }
        finally
        {
            dummy.Kill();
        }
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

    private string CreatePidFile(int pid, long startTimeTicks)
    {
        string fileName = $"roslyn-query-{Guid.NewGuid():N}.pid";
        string pidFilePath = Path.Combine(_stateDir, fileName);
        string content =
            pid.ToString(CultureInfo.InvariantCulture)
            + "\n"
            + startTimeTicks.ToString(CultureInfo.InvariantCulture)
            + "\n";
        File.WriteAllText(pidFilePath, content);
        _pidFilePaths.Add(pidFilePath);
        return pidFilePath;
    }

    private string CreatePidFileWithPidOnly(int pid)
    {
        string fileName = $"roslyn-query-{Guid.NewGuid():N}.pid";
        string pidFilePath = Path.Combine(_stateDir, fileName);
        File.WriteAllText(pidFilePath, pid.ToString(CultureInfo.InvariantCulture) + "\n");
        _pidFilePaths.Add(pidFilePath);
        return pidFilePath;
    }

    private string CreatePidFileWithContent(string content)
    {
        string fileName = $"roslyn-query-{Guid.NewGuid():N}.pid";
        string pidFilePath = Path.Combine(_stateDir, fileName);
        File.WriteAllText(pidFilePath, content);
        _pidFilePaths.Add(pidFilePath);
        return pidFilePath;
    }
}
