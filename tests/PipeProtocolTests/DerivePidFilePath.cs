using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.PipeProtocolTests;

public sealed class DerivePidFilePath
{
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
    public void AnyPath_CreatesContainingDirectory()
    {
        // Arrange
        string path = Path.Combine(Path.GetTempPath(), "MyApp.sln");

        // Act
        string pidFilePath = PipeProtocol.DerivePidFilePath(path);

        // Assert
        Directory.Exists(Path.GetDirectoryName(pidFilePath)).ShouldBeTrue();
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
}
