using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

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
    public async Task WhenDescribeWithoutType_ReturnsOne()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution: null!);

        // Act
        int exitCode = await CommandDispatcher.ExecuteAsync(["describe"], context);

        // Assert
        exitCode.ShouldBe(1);
        stderr.ToString().ShouldContain("describe requires a type name");
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

    [Fact]
    public async Task WhenCancelledToken_ThrowsOperationCanceledException()
    {
        // Arrange
        Solution solution = CreateSolutionWithSource("class C { }");
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await CommandDispatcher.ExecuteAsync(
                ["find-refs", "C"],
                context,
                cts.Token));
    }

    [Fact]
    public async Task WhenCancelledTokenAndListMembers_ThrowsOperationCanceledException()
    {
        // Arrange
        Solution solution = CreateSolutionWithSource("class C { }");
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await CommandDispatcher.ExecuteAsync(
                ["list-members", "C"],
                context,
                cts.Token));
    }

    [Fact]
    public async Task WhenCancelledTokenAndListTypes_ThrowsOperationCanceledException()
    {
        // Arrange
        Solution solution = CreateSolutionWithSource("namespace N { class C { } }");
        StringWriter stdout = new();
        StringWriter stderr = new();
        CommandContext context = new(stdout, stderr, solution);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await CommandDispatcher.ExecuteAsync(
                ["list-types", "N"],
                context,
                cts.Token));
    }

    private static Solution CreateSolutionWithSource(string source)
    {
        AdhocWorkspace workspace = new();
        ProjectInfo projectInfo = ProjectInfo.Create(
            ProjectId.CreateNewId(),
            VersionStamp.Default,
            "TestProject",
            "TestProject",
            LanguageNames.CSharp,
            metadataReferences:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            ]);
        Project project = workspace.AddProject(projectInfo);
        Document document = workspace.AddDocument(
            project.Id,
            "Test.cs",
            SourceText.From(source));
        return document.Project.Solution;
    }
}
