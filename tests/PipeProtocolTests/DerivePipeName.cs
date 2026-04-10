using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.PipeProtocolTests;

public sealed class DerivePipeName
{
    [Fact]
    public void SamePath_ReturnsSameName()
    {
        // Arrange
        string path = @"C:\Projects\MyApp\MyApp.sln";

        // Act
        string first = PipeProtocol.DerivePipeName(path);
        string second = PipeProtocol.DerivePipeName(path);

        // Assert
        first.ShouldBe(second);
    }

    [Fact]
    public void SamePathDifferentCasing_ReturnsSameName()
    {
        // Arrange
        string lower = @"c:\projects\myapp\myapp.sln";
        string upper = @"C:\Projects\MyApp\MyApp.sln";

        // Act
        string first = PipeProtocol.DerivePipeName(lower);
        string second = PipeProtocol.DerivePipeName(upper);

        // Assert
        first.ShouldBe(second);
    }

    [Fact]
    public void DifferentPaths_ReturnDifferentNames()
    {
        // Arrange
        string pathA = @"C:\Projects\AppA\AppA.sln";
        string pathB = @"C:\Projects\AppB\AppB.sln";

        // Act
        string nameA = PipeProtocol.DerivePipeName(pathA);
        string nameB = PipeProtocol.DerivePipeName(pathB);

        // Assert
        nameA.ShouldNotBe(nameB);
    }

    [Fact]
    public void Result_StartsWithPrefix()
    {
        // Arrange
        string path = @"C:\Projects\MyApp\MyApp.sln";

        // Act
        string name = PipeProtocol.DerivePipeName(path);

        // Assert
        name.ShouldStartWith("roslyn-query-");
    }
}
