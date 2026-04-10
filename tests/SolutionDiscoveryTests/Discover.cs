using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.SolutionDiscoveryTests;

public sealed class Discover
{
    [Fact]
    public void WhenSlnxInCurrentDir_ReturnsSlnxPath()
    {
        // Arrange
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        string slnxPath = Path.Combine(dir, "my.slnx");
        File.WriteAllText(slnxPath, "");
        StringWriter stderr = new();

        try
        {
            // Act
            string? result = SolutionDiscovery.Discover(dir, stderr);

            // Assert
            result.ShouldBe(slnxPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WhenSlnInCurrentDir_ReturnsSlnPath()
    {
        // Arrange
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        string slnPath = Path.Combine(dir, "my.sln");
        File.WriteAllText(slnPath, "");
        StringWriter stderr = new();

        try
        {
            // Act
            string? result = SolutionDiscovery.Discover(dir, stderr);

            // Assert
            result.ShouldBe(slnPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WhenSlnxInParentDir_ReturnsSlnxPath()
    {
        // Arrange
        string parent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        string child = Path.Combine(parent, "sub");
        Directory.CreateDirectory(child);
        string slnxPath = Path.Combine(parent, "my.slnx");
        File.WriteAllText(slnxPath, "");
        StringWriter stderr = new();

        try
        {
            // Act
            string? result = SolutionDiscovery.Discover(child, stderr);

            // Assert
            result.ShouldBe(slnxPath);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void WhenMultipleSlnFiles_ReturnsNullAndWritesError()
    {
        // Arrange
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a.sln"), "");
        File.WriteAllText(Path.Combine(dir, "b.sln"), "");
        StringWriter stderr = new();

        try
        {
            // Act
            string? result = SolutionDiscovery.Discover(dir, stderr);

            // Assert
            result.ShouldBeNull();
            stderr.ToString().ShouldContain("specify one explicitly");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WhenNoSolutionFound_ReturnsNullAndWritesError()
    {
        // Arrange
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        StringWriter stderr = new();

        try
        {
            // Act
            string? result = SolutionDiscovery.Discover(dir, stderr);

            // Assert
            result.ShouldBeNull();
            stderr.ToString().ShouldContain("No .sln");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
