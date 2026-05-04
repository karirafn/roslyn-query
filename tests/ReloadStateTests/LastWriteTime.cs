using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ReloadStateTests;

public sealed class LastWriteTime
{
    [Fact]
    public void ReflectsMaxWriteTimeOfTrackedPaths()
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

            using AdhocWorkspace workspace = new();
            Solution solution = workspace.CurrentSolution;

            // Act
            ReloadState sut = new(solution, [fileA, fileB]);

            // Assert
            sut.LastWriteTime.ShouldBe(laterTime);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WhenTrackedFileDeleted_LastWriteTimeIsMaxValue()
    {
        // Arrange
        string missingPath = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString("N"),
            "Missing.csproj");

        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;

        // Act
        ReloadState sut = new(solution, [missingPath]);

        // Assert
        sut.LastWriteTime.ShouldBe(DateTime.MaxValue);
    }
}
