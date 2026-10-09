using System.Globalization;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonIntegrationTests;

public sealed class StalePidFile : IDisposable
{
    private readonly string _solutionPath;
    private readonly string _stateDir;

    public StalePidFile()
    {
        _stateDir = Path.Combine(Path.GetTempPath(), $"rq-test-stalepid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_stateDir);
        PipeProtocol.SetStateDirectoryOverrideForTests(_stateDir);
        _solutionPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sln");
    }

    [Fact]
    public void WhenPidIsDead_IsProcessRunningReturnsFalse()
    {
        // Arrange
        int stalePid = int.MaxValue;

        // Act
        bool running = DaemonProcess.IsProcessRunning(stalePid);

        // Assert
        running.ShouldBeFalse();
    }

    [Fact]
    public async Task StopDaemon_CleansUpStalePidFile()
    {
        // Arrange — single-line legacy PID file with a dead PID
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        await File.WriteAllTextAsync(pidFilePath, int.MaxValue.ToString(CultureInfo.InvariantCulture));
        File.Exists(pidFilePath).ShouldBeTrue();

        // Act — pipe unreachable (no server); dead PID → ArgumentException → stale → deleted
        await DaemonProcess.StopDaemon(_solutionPath);

        // Assert
        File.Exists(pidFilePath).ShouldBeFalse();
    }

    [Fact]
    public async Task StopDaemon_WithStalePid_DoesNotThrow()
    {
        // Arrange
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        await File.WriteAllTextAsync(pidFilePath, int.MaxValue.ToString(CultureInfo.InvariantCulture));

        // Act & Assert
        await Should.NotThrowAsync(() => DaemonProcess.StopDaemon(_solutionPath));
    }

    [Fact]
    public void ReadPidFile_ReturnsStalePid()
    {
        // Arrange
        int stalePid = int.MaxValue;
        string pidFilePath = PipeProtocol.DerivePidFilePath(_solutionPath);
        File.WriteAllText(pidFilePath, stalePid.ToString(CultureInfo.InvariantCulture));

        // Act
        int? readPid = DaemonProcess.ReadPidFile(_solutionPath);

        // Assert
        readPid.ShouldBe(stalePid);
    }

    public void Dispose()
    {
        DaemonProcess.CleanupPidFile(_solutionPath);
        PipeProtocol.SetStateDirectoryOverrideForTests(null);

        if (Directory.Exists(_stateDir))
        {
            foreach (string file in Directory.EnumerateFiles(_stateDir))
            {
                File.Delete(file);
            }

            Directory.Delete(_stateDir);
        }
    }
}
