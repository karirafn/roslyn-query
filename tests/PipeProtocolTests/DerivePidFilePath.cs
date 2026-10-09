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
    public void AnyPath_DoesNotCreateContainingDirectory()
    {
        // Arrange
        string path = Path.Combine(Path.GetTempPath(), "MyApp.sln");
        string stateDirectory = PipeProtocol.GetStateDirectory();
        bool existedBefore = Directory.Exists(stateDirectory);

        // Act
        string pidFilePath = PipeProtocol.DerivePidFilePath(path);

        // Assert — directory existence must not change; derivation is pure
        string containingDir = Path.GetDirectoryName(pidFilePath).ShouldNotBeNull();
        containingDir.ShouldBe(stateDirectory);
        Directory.Exists(stateDirectory).ShouldBe(existedBefore);
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
