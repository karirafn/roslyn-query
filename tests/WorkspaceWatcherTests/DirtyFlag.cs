using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.WorkspaceWatcherTests;

public sealed class DirtyFlag
{
    // Use a real temp directory so WorkspaceWatcher can create the FileSystemWatcher without error.
    private readonly string _dir;

    public DirtyFlag()
    {
        _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void WhenNothingDirty_ConsumeDirtyReturnsFalse()
    {
        // Arrange
        using WorkspaceWatcher sut = new(_dir);

        // Act
        bool result = sut.ConsumeDirty();

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public void WhenMarkDirtyCalled_ConsumeDirtyReturnsTrue()
    {
        // Arrange
        using WorkspaceWatcher sut = new(_dir);

        // Act
        sut.MarkDirty();
        bool result = sut.ConsumeDirty();

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void WhenConsumeDirtyCalledTwice_SecondCallReturnsFalse()
    {
        // Arrange
        using WorkspaceWatcher sut = new(_dir);
        sut.MarkDirty();
        sut.ConsumeDirty(); // consume the flag

        // Act
        bool result = sut.ConsumeDirty();

        // Assert — flag was cleared by first consume; second call returns false
        result.ShouldBeFalse();
    }

    [Theory]
    [InlineData(WatcherChangeTypes.Created, "Foo.cs", true)]
    [InlineData(WatcherChangeTypes.Deleted, "Foo.cs", true)]
    [InlineData(WatcherChangeTypes.Renamed, "Foo.cs", true)]
    [InlineData(WatcherChangeTypes.Created, "Foo.csproj", true)]
    [InlineData(WatcherChangeTypes.Created, "packages.lock.json", true)]
    [InlineData(WatcherChangeTypes.Created, "Foo.txt", false)]
    public void WhenHandleChangeCalled_DirtyFlagMatchesExpected(
        WatcherChangeTypes changeType,
        string fileName,
        bool expectedDirty)
    {
        // Arrange
        using WorkspaceWatcher sut = new(_dir);
        string path = Path.Combine(_dir, fileName);

        // Act
        sut.HandleChange(changeType, path);

        // Assert
        sut.ConsumeDirty().ShouldBe(expectedDirty);
    }

    [Fact]
    public void WhenChangedEventOnCsFile_DoesNotSetDirty()
    {
        // Arrange — .cs content edits go through incremental mtime-polling; must not force full reload
        using WorkspaceWatcher sut = new(_dir);
        string path = Path.Combine(_dir, "Foo.cs");

        // Act
        sut.HandleChange(WatcherChangeTypes.Changed, path);

        // Assert
        sut.ConsumeDirty().ShouldBeFalse();
    }

    [Fact]
    public void WhenChangedEventOnCsprojFile_SetsDirty()
    {
        // Arrange — project file changes are structural
        using WorkspaceWatcher sut = new(_dir);
        string path = Path.Combine(_dir, "Alpha.csproj");

        // Act
        sut.HandleChange(WatcherChangeTypes.Changed, path);

        // Assert
        sut.ConsumeDirty().ShouldBeTrue();
    }

    [Fact]
    public void WhenChangedEventOnPropsFile_SetsDirty()
    {
        // Arrange
        using WorkspaceWatcher sut = new(_dir);
        string path = Path.Combine(_dir, "Directory.Build.props");

        // Act
        sut.HandleChange(WatcherChangeTypes.Changed, path);

        // Assert
        sut.ConsumeDirty().ShouldBeTrue();
    }

    [Fact]
    public void WhenChangedEventOnPackagesLockJson_SetsDirty()
    {
        // Arrange
        using WorkspaceWatcher sut = new(_dir);
        string path = Path.Combine(_dir, "packages.lock.json");

        // Act
        sut.HandleChange(WatcherChangeTypes.Changed, path);

        // Assert
        sut.ConsumeDirty().ShouldBeTrue();
    }

    [Fact]
    public void WhenChangedEventOnSlnxFile_SetsDirty()
    {
        // Arrange
        using WorkspaceWatcher sut = new(_dir);
        string path = Path.Combine(_dir, "Solution.slnx");

        // Act
        sut.HandleChange(WatcherChangeTypes.Changed, path);

        // Assert
        sut.ConsumeDirty().ShouldBeTrue();
    }

    [Fact]
    public void WhenCreatedInBinDirectory_DoesNotSetDirty()
    {
        // Arrange — output directory events must be suppressed
        using WorkspaceWatcher sut = new(_dir);
        string path = Path.Combine(_dir, "bin", "Debug", "Foo.cs");

        // Act
        sut.HandleChange(WatcherChangeTypes.Created, path);

        // Assert
        sut.ConsumeDirty().ShouldBeFalse();
    }

    [Fact]
    public void WhenMarkDirtyCalledMultipleTimes_ConsumeDirtyReturnsTrueOnce()
    {
        // Arrange
        using WorkspaceWatcher sut = new(_dir);

        // Act
        sut.MarkDirty();
        sut.MarkDirty();
        bool firstConsume = sut.ConsumeDirty();
        bool secondConsume = sut.ConsumeDirty();

        // Assert — flag is binary, not a counter; second consume is false
        firstConsume.ShouldBeTrue();
        secondConsume.ShouldBeFalse();
    }
}
