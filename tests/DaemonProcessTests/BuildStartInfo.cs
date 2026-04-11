using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonProcessTests;

public sealed class BuildStartInfo
{
    [Fact]
    public void AnySolutionPath_ArgumentListContainsDaemonFlagAndPath()
    {
        // Arrange
        string solutionPath = @"C:\projects\MyApp.sln";

        // Act
        System.Diagnostics.ProcessStartInfo result = DaemonProcess.BuildStartInfo(solutionPath);

        // Assert
        result.ArgumentList.ShouldBe(["--daemon", solutionPath]);
        result.Arguments.ShouldBeEmpty();
    }

    [Fact]
    public void PathContainingDoubleQuote_ArgumentListPreservesRawPath()
    {
        // Arrange
        string solutionPath = @"C:\proj\my""evil.sln";

        // Act
        System.Diagnostics.ProcessStartInfo result = DaemonProcess.BuildStartInfo(solutionPath);

        // Assert
        result.ArgumentList[1].ShouldBe(solutionPath);
    }
}
