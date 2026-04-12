using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.BatchFileReaderTests;

public sealed class ResolveFilePath
{
    [Fact]
    public void WhenNoNonFlagArgs_ReturnsNull()
    {
        // Arrange
        string[] args = ["batch", "--quiet"];

        // Act
        string? result = BatchFileReader.ResolveFilePath(args);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public void WhenOnlySolutionArg_ReturnsNull()
    {
        // Arrange
        string[] args = ["batch", "my.sln"];

        // Act
        string? result = BatchFileReader.ResolveFilePath(args);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public void WhenFileArgPresent_ReturnsFilePath()
    {
        // Arrange
        string[] args = ["batch", "queries.txt"];

        // Act
        string? result = BatchFileReader.ResolveFilePath(args);

        // Assert
        result.ShouldBe("queries.txt");
    }

    [Fact]
    public void WhenFileArgAlongsideSolutionAndFlags_ReturnsFilePath()
    {
        // Arrange
        string[] args = ["batch", "--quiet", "my.sln", "queries.txt"];

        // Act
        string? result = BatchFileReader.ResolveFilePath(args);

        // Assert
        result.ShouldBe("queries.txt");
    }

    [Fact]
    public void WhenSlnxExtension_IsNotTreatedAsFileArg()
    {
        // Arrange
        string[] args = ["batch", "my.slnx"];

        // Act
        string? result = BatchFileReader.ResolveFilePath(args);

        // Assert
        result.ShouldBeNull();
    }
}
