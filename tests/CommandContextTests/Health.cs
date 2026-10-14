using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.CommandContextTests;

public sealed class Health
{
    [Fact]
    public void WhenCreatedWithoutHealth_HealthIsComplete()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        Solution solution = null!;

        // Act
        CommandContext context = new(stdout, stderr, solution);

        // Assert
        context.Health.ShouldBe(LoadHealth.Complete);
    }

    [Fact]
    public void WhenCreatedWithPartialHealth_ExposesItUnchanged()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        Solution solution = null!;
        LoadHealth partialHealth = new(["project.csproj failed to load"]);

        // Act
        CommandContext context = new(stdout, stderr, solution, health: partialHealth);

        // Assert
        context.Health.ShouldBe(partialHealth);
    }
}
