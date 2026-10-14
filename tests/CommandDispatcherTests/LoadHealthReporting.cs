using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.CommandDispatcherTests;

public sealed class LoadHealthReporting
{
    // 5a: partial load notice on non-list-projects command
    [Fact]
    public async Task WhenPartialLoad_NonListProjectsCommand_PrintsNoticeOnStderr()
    {
        // Arrange
        Solution solution = CreateEmptySolution();
        StringWriter stdout = new();
        StringWriter stderr = new();
        LoadHealth health = new(["Project dropped: missing SDK"]);
        CommandContext context = new(stdout, stderr, solution, health: health);

        // Act
        await CommandDispatcher.ExecuteAsync(["find-refs", "SomeSymbol"], context);

        // Assert
        stderr.ToString().ShouldContain(
            "workspace: partial - 1 load failure; results may be incomplete (see list-projects)");
    }

    // 5b: complete load produces no extra notice
    [Fact]
    public async Task WhenCompleteLoad_NonListProjectsCommand_PrintsNoHealthNotice()
    {
        // Arrange
        Solution solution = CreateEmptySolution();
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);

        // Act
        await CommandDispatcher.ExecuteAsync(["find-refs", "SomeSymbol"], context);

        // Assert
        stderr.ToString().ShouldNotContain("workspace: partial");
        stderr.ToString().ShouldNotContain("load failure");
    }

    // 5c: --quiet does NOT suppress the one-line notice
    [Fact]
    public async Task WhenPartialLoadAndQuiet_NonListProjectsCommand_StillPrintsNotice()
    {
        // Arrange
        Solution solution = CreateEmptySolution();
        StringWriter stdout = new();
        StringWriter stderr = new();
        LoadHealth health = new(["drop1", "drop2"]);
        CommandContext context = new(stdout, stderr, solution, health: health);

        // Act
        await CommandDispatcher.ExecuteAsync(["find-refs", "SomeSymbol", "--quiet"], context);

        // Assert
        stderr.ToString().ShouldContain(
            "workspace: partial - 2 load failures; results may be incomplete (see list-projects)");
    }

    // 5d: --count nulls effectiveContext.Stderr; notice must reach real stderr via context.Stderr
    [Fact]
    public async Task WhenPartialLoadAndCount_NoticeReachesRealStderr()
    {
        // Arrange
        Solution solution = CreateEmptySolution();
        StringWriter stdout = new();
        StringWriter stderr = new();
        LoadHealth health = new(["dropped"]);
        CommandContext context = new(stdout, stderr, solution, health: health);

        // Act
        await CommandDispatcher.ExecuteAsync(["find-refs", "SomeSymbol", "--count"], context);

        // Assert
        stderr.ToString().ShouldContain(
            "workspace: partial - 1 load failure; results may be incomplete (see list-projects)");
    }

    private static Solution CreateEmptySolution()
    {
        AdhocWorkspace workspace = new();
        return workspace.CurrentSolution;
    }
}
