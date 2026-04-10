using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.CommandDispatcherTests;

public sealed class ExecuteAsync
{
    [Fact]
    public async Task WhenNoArgs_PrintsUsageAndReturnsOne()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution: null!);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync([], context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("Usage:");
    }

    [Fact]
    public async Task WhenUnknownCommand_ReturnsOne()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution: null!);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(["bogus-command"], context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("Unknown command: bogus-command");
    }

    [Fact]
    public async Task WhenFindRefsWithoutSymbol_ReturnsOne()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution: null!);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(["find-refs"], context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("find-refs requires a symbol name");
    }

    [Fact]
    public async Task WhenFindImplWithoutType_ReturnsOne()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution: null!);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(["find-impl"], context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("find-impl requires a type name");
    }

    [Fact]
    public async Task WhenFindCallersWithoutSymbol_ReturnsOne()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution: null!);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(["find-callers"], context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("find-callers requires a symbol name");
    }

    [Fact]
    public async Task WhenListMembersWithoutType_ReturnsOne()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution: null!);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(["list-members"], context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("list-members requires a type name");
    }

    [Fact]
    public async Task WhenListTypesWithoutNamespace_ReturnsOne()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution: null!);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(["list-types"], context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("list-types requires a namespace");
    }

    [Fact]
    public async Task WhenFindCtorWithoutType_ReturnsOne()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution: null!);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(["find-ctor"], context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("find-ctor requires a type name");
    }

    [Fact]
    public async Task WhenFindOverridesWithoutMember_ReturnsOne()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution: null!);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(["find-overrides"], context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("find-overrides requires a member name");
    }

    [Fact]
    public async Task WhenFindAttributeWithoutName_ReturnsOne()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution: null!);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(["find-attribute"], context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("find-attribute requires an attribute name");
    }

    [Fact]
    public async Task WhenFindBaseWithoutType_ReturnsOne()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution: null!);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(["find-base"], context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("find-base requires a type name");
    }

    [Fact]
    public async Task WhenFlagsOnly_PrintsUsageAndReturnsOne()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution: null!);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["--quiet", "--context"],
            context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("Usage:");
    }
}
