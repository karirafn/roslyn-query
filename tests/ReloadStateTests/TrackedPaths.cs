using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ReloadStateTests;

public sealed class TrackedPaths
{
    [Fact]
    public void WhenConstructed_ExposesTrackedPaths()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        IReadOnlyList<string> trackedPaths = [@"C:\projects\Alpha\Alpha.csproj"];

        // Act
        ReloadState sut = new(solution, trackedPaths);

        // Assert
        sut.TrackedPaths.ShouldBe(trackedPaths);
    }

    [Fact]
    public void WhenCompleteReloadCalled_UpdatesTrackedPaths()
    {
        // Arrange
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            string initialPath = Path.Combine(dir, "Alpha.csproj");
            string updatedPath = Path.Combine(dir, "Beta.csproj");
            File.WriteAllText(initialPath, "<Project />");
            File.WriteAllText(updatedPath, "<Project />");

            using AdhocWorkspace workspace = new();
            Solution solution = workspace.CurrentSolution;

            ReloadState sut = new(solution, [initialPath]);

            IReadOnlyList<string> updatedPaths = [updatedPath];

            // Act
            sut.TryBeginReload().ShouldBeTrue();
            sut.CompleteReload(solution, updatedPaths);

            // Assert
            sut.TrackedPaths.ShouldBe(updatedPaths);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
