using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.TrackedFilesTests;

public sealed class DetectChangedDocuments
{
    [Fact]
    public void WhenBaselineIsEmpty_ReturnsNoChanges()
    {
        // Arrange
        Dictionary<string, DateTime> baseline = [];

        // Act
        (bool StructuralChange, IReadOnlyList<(string Path, DateTime WriteTime)> Edited) result
            = TrackedFiles.DetectChangedDocuments(baseline);

        // Assert
        result.ShouldSatisfyAllConditions(
            () => result.StructuralChange.ShouldBeFalse(),
            () => result.Edited.ShouldBeEmpty());
    }

    [Fact]
    public void WhenMixedChanges_ReportsStructuralChangeAndEditedPaths()
    {
        // Arrange
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            // One existing file that was edited
            string editedPath = Path.Combine(dir, "Edited.cs");
            File.WriteAllText(editedPath, "");
            DateTime editedNewTime = new(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(editedPath, editedNewTime);

            // One existing file that is unchanged
            string unchangedPath = Path.Combine(dir, "Unchanged.cs");
            File.WriteAllText(unchangedPath, "");
            DateTime unchangedTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(unchangedPath, unchangedTime);

            // One path that no longer exists on disk
            string deletedPath = Path.Combine(dir, "Deleted.cs");

            Dictionary<string, DateTime> baseline = new()
            {
                [editedPath] = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                [unchangedPath] = unchangedTime,
                [deletedPath] = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };

            // Act
            (bool StructuralChange, IReadOnlyList<(string Path, DateTime WriteTime)> Edited) result
                = TrackedFiles.DetectChangedDocuments(baseline);

            // Assert
            result.ShouldSatisfyAllConditions(
                () => result.StructuralChange.ShouldBeTrue(),
                () => result.Edited.Count.ShouldBe(1),
                () => result.Edited[0].Path.ShouldBe(editedPath),
                () => result.Edited[0].WriteTime.ShouldBe(editedNewTime));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WhenFileWriteTimeMatchesBaseline_NoChangesReported()
    {
        // Arrange
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            string filePath = Path.Combine(dir, "Unchanged.cs");
            File.WriteAllText(filePath, "");

            DateTime baselineTime = new(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(filePath, baselineTime);

            Dictionary<string, DateTime> baseline = new()
            {
                [filePath] = baselineTime,
            };

            // Act
            (bool StructuralChange, IReadOnlyList<(string Path, DateTime WriteTime)> Edited) result
                = TrackedFiles.DetectChangedDocuments(baseline);

            // Assert
            result.ShouldSatisfyAllConditions(
                () => result.StructuralChange.ShouldBeFalse(),
                () => result.Edited.ShouldBeEmpty());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WhenFileWriteTimeAdvancedPastBaseline_AppearsInEditedList()
    {
        // Arrange
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            string filePath = Path.Combine(dir, "Foo.cs");
            File.WriteAllText(filePath, "");

            DateTime baselineTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            DateTime newWriteTime = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(filePath, newWriteTime);

            Dictionary<string, DateTime> baseline = new()
            {
                [filePath] = baselineTime,
            };

            // Act
            (bool StructuralChange, IReadOnlyList<(string Path, DateTime WriteTime)> Edited) result
                = TrackedFiles.DetectChangedDocuments(baseline);

            // Assert
            result.ShouldSatisfyAllConditions(
                () => result.StructuralChange.ShouldBeFalse(),
                () => result.Edited.Count.ShouldBe(1),
                () => result.Edited[0].Path.ShouldBe(filePath),
                () => result.Edited[0].WriteTime.ShouldBe(newWriteTime));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WhenBaselinePathNoLongerExistsOnDisk_StructuralChangeIsTrue()
    {
        // Arrange — a path that never existed on disk (sentinel will be returned by GetLastWriteTimeUtc)
        string missingPath = Path.Combine(
            Path.GetTempPath(),
            Guid.NewGuid().ToString("N"),
            "Deleted.cs");

        Dictionary<string, DateTime> baseline = new()
        {
            [missingPath] = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        // Act
        (bool StructuralChange, IReadOnlyList<(string Path, DateTime WriteTime)> Edited) result
            = TrackedFiles.DetectChangedDocuments(baseline);

        // Assert
        result.StructuralChange.ShouldBeTrue();
    }
}
