using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.PipeProtocolTests;

public sealed class DerivePidFilePath : IDisposable
{
    public DerivePidFilePath()
    {
        PipeProtocol.SetStateDirectoryOverrideForTests(null);
    }

    [Fact]
    public void AnyPath_IsUnderPerUserLocalApplicationData()
    {
        // Arrange
        string path = Path.Combine(Path.GetTempPath(), "MyApp.sln");
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Act
        string pidFilePath = PipeProtocol.DerivePidFilePath(path);

        // Assert
        pidFilePath.ShouldStartWith(localAppData);
    }

    [Fact]
    public void AnyPath_DoesNotCreateContainingDirectory()
    {
        // Arrange — point state directory at a unique path that is guaranteed not to exist
        string absentDir = Path.Combine(Path.GetTempPath(), $"rq-absent-{Guid.NewGuid():N}");
        PipeProtocol.SetStateDirectoryOverrideForTests(absentDir);
        string path = Path.Combine(Path.GetTempPath(), "MyApp.sln");

        // Act
        string pidFilePath = PipeProtocol.DerivePidFilePath(path);

        // Assert — derivation is pure: calling it must not create the directory
        string containingDir = Path.GetDirectoryName(pidFilePath).ShouldNotBeNull();
        containingDir.ShouldBe(absentDir);
        Directory.Exists(absentDir).ShouldBeFalse();
    }

    [Fact]
    public void AnyPath_FileNameStartsWithPrefix()
    {
        // Arrange
        string path = Path.Combine(Path.GetTempPath(), "MyApp.sln");

        // Act
        string pidFilePath = PipeProtocol.DerivePidFilePath(path);

        // Assert
        Path.GetFileName(pidFilePath).ShouldStartWith("roslyn-query-");
    }

    public void Dispose()
    {
        PipeProtocol.SetStateDirectoryOverrideForTests(null);
    }
}
