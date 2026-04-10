using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.CommandContextTests;

public sealed class Constructor
{
    [Fact]
    public void WhenCreated_ExposesProvidedWriters()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        Solution solution = null!;

        // Act
        CommandContext context = new(stdout, stderr, solution);

        // Assert
        context.Stdout.ShouldBeSameAs(stdout);
        context.Stderr.ShouldBeSameAs(stderr);
        context.Solution.ShouldBeNull();
    }
}
