using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis.MSBuild;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.SolutionLoaderTests;

public sealed class LoadWithHealth
{
    private static readonly string FixtureSlnxPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "MinimalSolution", "MinimalSolution.slnx"));

    [Fact]
    public void BuildHealth_WhenFailuresIsEmpty_ReturnsComplete()
    {
        // Arrange
        string[] failures = [];

        // Act
        LoadHealth health = SolutionLoader.BuildHealth(failures);

        // Assert
        health.ShouldBeSameAs(LoadHealth.Complete);
    }

    [Fact]
    public void BuildHealth_WhenFailuresIsNonEmpty_ReturnsPartialWithMessages()
    {
        // Arrange
        string[] failures = ["MSB4025: project was not loaded", "SDK not found"];

        // Act
        LoadHealth health = SolutionLoader.BuildHealth(failures);

        // Assert
        health.ShouldSatisfyAllConditions(
            () => health.IsPartial.ShouldBeTrue(),
            () => health.FailureCount.ShouldBe(2),
            () => health.FailureMessages.ShouldContain("MSB4025: project was not loaded"),
            () => health.FailureMessages.ShouldContain("SDK not found"));
    }

    [Fact]
    public async Task LoadWithHealthAsync_WhenSolutionLoadsCleanly_ReturnsComplete()
    {
        // Arrange — MSBuildLocator must be registered before creating a workspace.
        // The Program entry point normally does this; in tests, register once safely.
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }

        using MSBuildWorkspace workspace = MSBuildWorkspace.Create();

        // Act
        LoadHealth health = await SolutionLoader.LoadWithHealthAsync(
            workspace,
            FixtureSlnxPath,
            CancellationToken.None);

        // Assert
        health.IsPartial.ShouldBeFalse();
        health.FailureCount.ShouldBe(0);
    }
}
