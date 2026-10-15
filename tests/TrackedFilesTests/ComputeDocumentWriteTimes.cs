using System.Collections.Frozen;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.TrackedFilesTests;

public sealed class ComputeDocumentWriteTimes
{
    [Fact]
    public void WhenDocumentPathsIsEmpty_ReturnsEmptyDictionary()
    {
        // Arrange
        FrozenSet<string> paths = FrozenSet<string>.Empty;

        // Act
        FrozenDictionary<string, DateTime> result = TrackedFiles.ComputeDocumentWriteTimes(paths);

        // Assert
        result.ShouldBeEmpty();
    }

    [Fact]
    public void ReturnedDictionary_IsCaseInsensitive()
    {
        // Arrange
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            string filePath = Path.Combine(dir, "Foo.cs");
            File.WriteAllText(filePath, "");

            FrozenSet<string> paths = new[] { filePath }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

            // Act
            FrozenDictionary<string, DateTime> result = TrackedFiles.ComputeDocumentWriteTimes(paths);

            // Assert — lookup with a different casing should still find the entry
            result.ContainsKey(filePath.ToUpperInvariant()).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WhenFileDoesNotExist_ReturnsMissingFileSentinel()
    {
        // Arrange
        string missingPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Missing.cs");
        FrozenSet<string> paths = new[] { missingPath }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        // Act
        FrozenDictionary<string, DateTime> result = TrackedFiles.ComputeDocumentWriteTimes(paths);

        // Assert — File.GetLastWriteTimeUtc returns DateTime.FromFileTimeUtc(0) for missing files
        result[missingPath].ShouldBe(DateTime.FromFileTimeUtc(0));
    }

    [Fact]
    public void WhenFileExists_ReturnsItsUtcWriteTime()
    {
        // Arrange
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            string filePath = Path.Combine(dir, "Foo.cs");
            File.WriteAllText(filePath, "");
            DateTime expectedTime = new(2025, 3, 15, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(filePath, expectedTime);

            FrozenSet<string> paths = new[] { filePath }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

            // Act
            FrozenDictionary<string, DateTime> result = TrackedFiles.ComputeDocumentWriteTimes(paths);

            // Assert
            result[filePath].ShouldBe(expectedTime);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
