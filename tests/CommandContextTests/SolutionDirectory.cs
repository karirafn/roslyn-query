using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.CommandContextTests;

public sealed class SolutionDirectory
{
    [Fact]
    public void WhenCreatedWithSolutionDirectory_ExposesIt()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        Solution solution = null!;
        string solutionDirectory = @"C:\projects\myapp";

        // Act
        CommandContext context = new(stdout, stderr, solution, solutionDirectory);

        // Assert
        context.SolutionDirectory.ShouldBe(solutionDirectory);
    }
}
