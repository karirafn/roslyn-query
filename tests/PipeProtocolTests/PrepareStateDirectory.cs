using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.PipeProtocolTests;

public sealed class PrepareStateDirectory : IDisposable
{
    private readonly string _stateDir;

    public PrepareStateDirectory()
    {
        _stateDir = Path.Combine(Path.GetTempPath(), $"rq-test-prepare-{Guid.NewGuid()}");
        PipeProtocol.SetStateDirectoryOverrideForTests(_stateDir);
    }

    [Fact]
    public void WhenDirectoryAbsent_CreatesIt()
    {
        // Arrange — directory must not exist yet
        Directory.Exists(_stateDir).ShouldBeFalse();

        // Act
        PipeProtocol.PrepareStateDirectory();

        // Assert
        Directory.Exists(_stateDir).ShouldBeTrue();
    }

    [Fact]
    public void WhenDirectoryAbsent_CreatesWith0700()
    {
        // Unix-only: Windows uses ACLs, not POSIX mode bits.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange — directory must not exist yet
        Directory.Exists(_stateDir).ShouldBeFalse();

        // Act
        PipeProtocol.PrepareStateDirectory();

        // Assert
        UnixFileMode mode = File.GetUnixFileMode(_stateDir);
        mode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void WhenDirectoryExistsWithLooseMode_TightensTo0700()
    {
        // Unix-only: Windows uses ACLs, not POSIX mode bits.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange — pre-create the directory with a looser mode (0777)
        Directory.CreateDirectory(
            _stateDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        File.GetUnixFileMode(_stateDir).ShouldNotBe(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // Act
        PipeProtocol.PrepareStateDirectory();

        // Assert
        UnixFileMode mode = File.GetUnixFileMode(_stateDir);
        mode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void WhenDirectoryIsSymlink_Throws()
    {
        // Unix-only: symlink refusal is a security hardening relevant to Unix permission models.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange — create a real target directory, then a symlink at the state-dir path.
        string realTarget = _stateDir + "-real-target";
        Directory.CreateDirectory(realTarget);
        Directory.CreateSymbolicLink(_stateDir, realTarget);

        // Act & Assert
        IOException ex = Should.Throw<IOException>(() => PipeProtocol.PrepareStateDirectory());
        ex.Message.ShouldContain(_stateDir);
        ex.Message.ShouldContain("symbolic link");

        // Cleanup symlink and target
        Directory.Delete(_stateDir);
        Directory.Delete(realTarget);
    }

    public void Dispose()
    {
        PipeProtocol.SetStateDirectoryOverrideForTests(null);

        if (Directory.Exists(_stateDir))
        {
            Directory.Delete(_stateDir, recursive: true);
        }
    }
}
