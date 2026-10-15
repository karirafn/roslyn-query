using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.PipeProtocolTests;

public sealed class PrepareSocketDirectory : IDisposable
{
    private readonly string _socketDir;

    public PrepareSocketDirectory()
    {
        _socketDir = Path.Combine(Path.GetTempPath(), $"rq-test-sock-{Guid.NewGuid()}");
        PipeProtocol.SetSocketDirectoryOverrideForTests(_socketDir);
    }

    [Fact]
    public void WhenDirectoryAbsent_CreatesIt()
    {
        // Arrange — directory must not exist yet
        Directory.Exists(_socketDir).ShouldBeFalse();

        // Act
        PipeProtocol.PrepareSocketDirectory();

        // Assert
        Directory.Exists(_socketDir).ShouldBeTrue();
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
        Directory.Exists(_socketDir).ShouldBeFalse();

        // Act
        PipeProtocol.PrepareSocketDirectory();

        // Assert
        UnixFileMode mode = File.GetUnixFileMode(_socketDir);
        mode.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void WhenDirectoryExistsWithLooseMode_ThrowsIoException()
    {
        // Unix-only: Windows uses ACLs, not POSIX mode bits.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange — pre-create the directory with a looser mode (0777)
        Directory.CreateDirectory(
            _socketDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        File.GetUnixFileMode(_socketDir).ShouldNotBe(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // Act & Assert — a loose pre-existing socket dir is a squatting signal; reject it
        IOException ex = Should.Throw<IOException>(() => PipeProtocol.PrepareSocketDirectory());
        ex.Message.ShouldContain(_socketDir);
    }

    [Fact]
    public void WhenDirectoryIsSymlinkBeforeCreate_Throws()
    {
        // Unix-only: symlink refusal is a security hardening relevant to Unix permission models.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange — create a real target directory, then a symlink at the socket-dir path.
        string realTarget = _socketDir + "-real-target";
        Directory.CreateDirectory(realTarget);
        Directory.CreateSymbolicLink(_socketDir, realTarget);

        // Act & Assert
        IOException ex = Should.Throw<IOException>(() => PipeProtocol.PrepareSocketDirectory());
        ex.Message.ShouldContain(_socketDir);
        ex.Message.ShouldContain("symbolic link");
    }

    [Fact]
    public void WhenDirectoryAbsent_ReturnsDirectoryPath()
    {
        // Arrange — directory must not exist yet
        Directory.Exists(_socketDir).ShouldBeFalse();

        // Act
        string result = PipeProtocol.PrepareSocketDirectory();

        // Assert
        result.ShouldBe(_socketDir);
    }

    [Fact]
    public void WhenWindowsOs_CreatesDirectoryAndReturnsPath()
    {
        // This test documents the Windows no-op path; it only validates the core behavior.
        // On Linux/macOS the mode checks above cover the Unix path.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange — directory must not exist yet
        Directory.Exists(_socketDir).ShouldBeFalse();

        // Act
        string result = PipeProtocol.PrepareSocketDirectory();

        // Assert
        Directory.Exists(_socketDir).ShouldBeTrue();
        result.ShouldBe(_socketDir);
    }

    public void Dispose()
    {
        PipeProtocol.SetSocketDirectoryOverrideForTests(null);

        if (Directory.Exists(_socketDir))
        {
            Directory.Delete(_socketDir, recursive: true);
        }

        // Clean up any real-target dirs left behind by symlink tests
        string realTarget = _socketDir + "-real-target";
        if (Directory.Exists(realTarget))
        {
            Directory.Delete(realTarget, recursive: true);
        }
    }
}
