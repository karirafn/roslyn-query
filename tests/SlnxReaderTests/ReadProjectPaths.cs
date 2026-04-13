using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.SlnxReaderTests;

public sealed class ReadProjectPaths
{
    [Fact]
    public void WhenProjectsAtRoot_ReturnsAbsolutePaths()
    {
        // Arrange
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        string slnxPath = Path.Combine(dir, "solution.slnx");
        File.WriteAllText(slnxPath, """
            <Solution>
              <Project Path="src/Alpha/Alpha.csproj" />
              <Project Path="src/Beta/Beta.csproj" />
            </Solution>
            """);

        try
        {
            // Act
            IReadOnlyList<string> result = SlnxReader.ReadProjectPaths(slnxPath);

            // Assert
            result.ShouldBe(
            [
                Path.GetFullPath(Path.Combine(dir, "src/Alpha/Alpha.csproj")),
                Path.GetFullPath(Path.Combine(dir, "src/Beta/Beta.csproj")),
            ]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WhenProjectsInNestedFolders_ReturnsAllPaths()
    {
        // Arrange
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        string slnxPath = Path.Combine(dir, "solution.slnx");
        File.WriteAllText(slnxPath, """
            <Solution>
              <Project Path="src/App/App.csproj" />
              <Folder Name="Tests">
                <Project Path="tests/App.Tests/App.Tests.csproj" />
                <Folder Name="Integration">
                  <Project Path="tests/App.IntegrationTests/App.IntegrationTests.csproj" />
                </Folder>
              </Folder>
            </Solution>
            """);

        try
        {
            // Act
            IReadOnlyList<string> result = SlnxReader.ReadProjectPaths(slnxPath);

            // Assert
            result.ShouldBe(
            [
                Path.GetFullPath(Path.Combine(dir, "src/App/App.csproj")),
                Path.GetFullPath(Path.Combine(dir, "tests/App.Tests/App.Tests.csproj")),
                Path.GetFullPath(Path.Combine(dir, "tests/App.IntegrationTests/App.IntegrationTests.csproj")),
            ]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WhenNoProjects_ReturnsEmpty()
    {
        // Arrange
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        string slnxPath = Path.Combine(dir, "solution.slnx");
        File.WriteAllText(slnxPath, "<Solution></Solution>");

        try
        {
            // Act
            IReadOnlyList<string> result = SlnxReader.ReadProjectPaths(slnxPath);

            // Assert
            result.ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
