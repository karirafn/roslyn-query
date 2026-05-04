using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ReloadStateTests;

public sealed class IsStale
{
    [Fact]
    public async Task WhenTrackedFileIsNewerThanLastWriteTime_ReturnsTrue()
    {
        // Arrange
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            string csprojPath = Path.Combine(dir, "Alpha.csproj");
            await File.WriteAllTextAsync(csprojPath, "<Project />");

            DateTime oldTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(csprojPath, oldTime);

            using AdhocWorkspace workspace = new();
            Solution solution = workspace.CurrentSolution;

            ReloadState sut = new(solution, [csprojPath]);

            // Update the file after construction so it appears newer
            DateTime newTime = new(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(csprojPath, newTime);

            // Act
            bool result = await sut.IsStaleAsync();

            // Assert
            result.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task WhenTrackedFilesAreUnchanged_ReturnsFalse()
    {
        // Arrange
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            string csprojPath = Path.Combine(dir, "Alpha.csproj");
            await File.WriteAllTextAsync(csprojPath, "<Project />");

            DateTime fixedTime = new(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(csprojPath, fixedTime);

            using AdhocWorkspace workspace = new();
            Solution solution = workspace.CurrentSolution;

            ReloadState sut = new(solution, [csprojPath]);

            // Act
            bool result = await sut.IsStaleAsync();

            // Assert
            result.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
