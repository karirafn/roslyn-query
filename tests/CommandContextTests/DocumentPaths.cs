using System.Collections.Frozen;

using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.CommandContextTests;

public sealed class DocumentPaths
{
    [Fact]
    public void WhenCreatedWithDocumentPaths_ExposesProvidedSet()
    {
        // Arrange
        StringWriter stdout = new();
        StringWriter stderr = new();
        Solution solution = null!;
        List<string> pathList = [@"C:\src\Foo.cs", @"C:\src\Bar.cs"];
        FrozenSet<string> paths = pathList.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        // Act
        CommandContext context = new(stdout, stderr, solution, documentPaths: paths);

        // Assert
        context.DocumentPaths.ShouldBeSameAs(paths);
    }
}