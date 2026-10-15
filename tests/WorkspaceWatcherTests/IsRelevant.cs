using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.WorkspaceWatcherTests;

public sealed class IsRelevant
{
    [Theory]
    [InlineData("Foo.cs")]
    [InlineData("Foo.csproj")]
    [InlineData("Directory.Build.props")]
    [InlineData("Akraborg.slnx")]
    [InlineData("Akraborg.sln")]
    [InlineData("packages.lock.json")]
    public void WhenFileHasRelevantExtension_ReturnsTrue(string fileName)
    {
        // Arrange
        string path = Path.Combine("/workspace", "src", fileName);

        // Act
        bool result = WorkspaceWatcher.IsRelevant(path);

        // Assert
        result.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Foo.txt")]
    [InlineData("Foo.json")]
    [InlineData("Foo.xml")]
    [InlineData("Foo.dll")]
    [InlineData("Foo.exe")]
    [InlineData("other.lock.json")]
    public void WhenFileHasUnrelatedExtension_ReturnsFalse(string fileName)
    {
        // Arrange
        string path = Path.Combine("/workspace", "src", fileName);

        // Act
        bool result = WorkspaceWatcher.IsRelevant(path);

        // Assert
        result.ShouldBeFalse();
    }

    [Theory]
    [InlineData("/workspace/src/bin/Debug/Foo.cs")]
    [InlineData("/workspace/src/bin/Release/App.csproj")]
    [InlineData("/workspace/src/obj/Debug/Foo.cs")]
    [InlineData("/workspace/src/obj/project.assets.json")]
    [InlineData("/workspace/src/nested/bin/net8.0/Foo.dll")]
    [InlineData("/workspace/src/nested/obj/ref/Foo.cs")]
    public void WhenPathContainsBinOrObjSegment_ReturnsFalse(string path)
    {
        // Arrange (path already absolute)

        // Act
        bool result = WorkspaceWatcher.IsRelevant(path);

        // Assert
        result.ShouldBeFalse();
    }

    [Theory]
    [InlineData("/workspace/binaries/Foo.cs")]
    [InlineData("/workspace/objstore/Foo.cs")]
    [InlineData("/workspace/objection/Foo.cs")]
    [InlineData("/workspace/binary/Foo.cs")]
    public void WhenPathSegmentStartsWithBinOrObjButIsNotExact_ReturnsTrue(string path)
    {
        // Arrange (path uses bin/obj as prefix of a longer segment — must not match)

        // Act
        bool result = WorkspaceWatcher.IsRelevant(path);

        // Assert
        result.ShouldBeTrue();
    }

    [Theory]
    [InlineData("PACKAGES.LOCK.JSON")]
    [InlineData("Foo.CS")]
    [InlineData("Foo.CSPROJ")]
    [InlineData("Foo.SLN")]
    [InlineData("Foo.SLNX")]
    [InlineData("Foo.PROPS")]
    public void WhenFileExtensionIsUpperCase_ReturnsTrue(string fileName)
    {
        // Arrange — verify extension matching is case-insensitive
        string path = Path.Combine(Path.GetTempPath(), "solution", "src", fileName);

        // Act
        bool result = WorkspaceWatcher.IsRelevant(path);

        // Assert
        result.ShouldBeTrue();
    }
}
