using System.Diagnostics;
using System.Globalization;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonProcessTests;

public sealed class WritePidFile : IDisposable
{
    private readonly string _solutionPath;
    private readonly string _stateDir;

    public WritePidFile()
    {
        _stateDir = Path.Combine(Path.GetTempPath(), $"rq-test-writepid-{Guid.NewGuid():N}");
        PipeProtocol.SetStateDirectoryOverrideForTests(_stateDir);
        _solutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
    }

    [Fact]
    public void WritesCurrentProcessId()
    {
        // Arrange & Act
        DaemonProcess.WritePidFile(_solutionPath);

        // Assert
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        string[] lines = File.ReadAllLines(pidFilePath);
        lines.Length.ShouldBeGreaterThanOrEqualTo(2);
        int pid = int.Parse(lines[0], CultureInfo.InvariantCulture);
        pid.ShouldBe(Environment.ProcessId);
        long startTimeTicks = long.Parse(lines[1], CultureInfo.InvariantCulture);
        startTimeTicks.ShouldBe(Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);
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
        Directory.CreateDirectory(_stateDir);
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        File.WriteAllText(pidFilePath, "not-a-number");

        // Act
        int? pid = DaemonProcess.ReadPidFile(_solutionPath);

        // Assert
        pid.ShouldBeNull();
    }

    [Fact]
    public void OnUnix_PidFileModeIs0600()
    {
        // Unix-only: Windows uses ACLs, not POSIX mode bits.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange & Act
        DaemonProcess.WritePidFile(_solutionPath);

        // Assert
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        UnixFileMode mode = File.GetUnixFileMode(pidFilePath);
        mode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void WhenPidFilePathIsSymlink_Throws()
    {
        // Unix-only: symlink refusal is a security hardening relevant to Unix permission models.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange — create the state directory, create a real file as the symlink target,
        // then create a symlink at the expected PID file path.
        Directory.CreateDirectory(_stateDir);
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        string realTarget = pidFilePath + ".real";
        File.WriteAllText(realTarget, "");
        File.CreateSymbolicLink(pidFilePath, realTarget);

        // Act & Assert
        IOException ex = Should.Throw<IOException>(() => DaemonProcess.WritePidFile(_solutionPath));
        ex.Message.ShouldContain(pidFilePath);
        ex.Message.ShouldContain("symlink");
    }

    [Fact]
    public void ReadPidRecord_WhenLegacySingleLine_ReturnsPidAndNullStartTime()
    {
        // Arrange — write a single-line PID file (legacy format)
        Directory.CreateDirectory(_stateDir);
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        const int LegacyPid = 12345;
        File.WriteAllText(pidFilePath, LegacyPid.ToString(CultureInfo.InvariantCulture));

        // Act
        (int Pid, long? StartTimeTicks)? record = DaemonProcess.ReadPidRecord(_solutionPath);

        // Assert
        record.ShouldNotBeNull();
        record.Value.ShouldSatisfyAllConditions(
            () => record.Value.Pid.ShouldBe(LegacyPid),
            () => record.Value.StartTimeTicks.ShouldBeNull());
    }

    [Fact]
    public void ReadPidRecord_WhenTwoLines_ReturnsPidAndStartTime()
    {
        // Arrange
        DaemonProcess.WritePidFile(_solutionPath);

        // Act
        (int Pid, long? StartTimeTicks)? record = DaemonProcess.ReadPidRecord(_solutionPath);

        // Assert
        record.ShouldNotBeNull();
        record.Value.ShouldSatisfyAllConditions(
            () => record.Value.Pid.ShouldBe(Environment.ProcessId),
            () => record.Value.StartTimeTicks.ShouldNotBeNull(),
            () => record.Value.StartTimeTicks!.Value.ShouldBe(
                Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks));
    }

    public void Dispose()
    {
        PipeProtocol.SetStateDirectoryOverrideForTests(null);
        DaemonProcess.CleanupPidFile(_solutionPath);

        if (Directory.Exists(_stateDir))
        {
            // Clean all PID files and the directory
            foreach (string file in Directory.EnumerateFiles(_stateDir))
            {
                File.Delete(file);
            }

            Directory.Delete(_stateDir);
        }
    }
}
