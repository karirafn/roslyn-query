using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.TrackedFilesTests;

public sealed class ComputeMaxWriteTime
{
    [Fact]
    public void WhenPathsListIsEmpty_ReturnsMinValue()
    {
        // Act
        DateTime result = TrackedFiles.ComputeMaxWriteTime([]);

        // Assert
        result.ShouldBe(DateTime.MinValue);
    }

    [Fact]
    public void WhenFileIsMissing_ReturnsMaxValue()
    {
        // Arrange
        string missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Missing.csproj");

        // Act
        DateTime result = TrackedFiles.ComputeMaxWriteTime([missingPath]);

        // Assert
        result.ShouldBe(DateTime.MaxValue);
    }

    [Fact]
    public void WhenMultipleFiles_ReturnsLatestWriteTime()
    {
        // Arrange
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            string fileA = Path.Combine(dir, "A.csproj");
            string fileB = Path.Combine(dir, "B.csproj");

            File.WriteAllText(fileA, "");
            File.WriteAllText(fileB, "");

            DateTime earlierTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime laterTime = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(fileA, earlierTime);
            File.SetLastWriteTimeUtc(fileB, laterTime);

            // Act
            DateTime result = TrackedFiles.ComputeMaxWriteTime([fileA, fileB]);

            // Assert
            result.ShouldBe(laterTime);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
