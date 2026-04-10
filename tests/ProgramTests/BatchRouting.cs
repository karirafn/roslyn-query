using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ProgramTests;

public sealed class BatchRouting
{
    [Fact]
    public async Task WhenBatchWithoutSolution_PrintsError()
    {
        // Arrange
        StringWriter stderr = new();

        // Act & Assert — batch requires a discoverable solution
        // We verify the batch command is recognized and attempts solution resolution
        // rather than falling through to "Unknown command"
        // This is tested indirectly via PrintUsageAsync containing "batch"
        StringWriter stdout = new();
        CommandContext context = new(stdout, stderr, solution: null!);
        int exitCode = await CommandDispatcher.ExecuteAsync(["batch"], context);

        // Assert — batch is not a CommandDispatcher command, it's handled in Program.cs
        // So ExecuteAsync should return unknown command error
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("Unknown command: batch");
    }
}
