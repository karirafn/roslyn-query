using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.CommandDispatcherTests;

public sealed class FlagParsing
{
    [Fact]
    public async Task WhenLimitFlagProvided_StripsLimitAndValueFromArgs()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution: null!);

        // Act — find-refs with --limit 10 but no symbol should error about missing symbol
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["find-refs", "--limit", "10"],
            context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("find-refs requires a symbol name");
    }

    [Fact]
    public async Task WhenAbsoluteFlagProvided_StripsFromArgs()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution: null!);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["find-refs", "--absolute"],
            context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("find-refs requires a symbol name");
    }

    [Fact]
    public async Task WhenCompactFlagProvided_StripsFromArgs()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution: null!);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(
            ["find-refs", "--compact"],
            context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("find-refs requires a symbol name");
    }
}
