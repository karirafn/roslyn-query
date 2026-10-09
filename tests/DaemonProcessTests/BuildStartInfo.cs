using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonProcessTests;

public sealed class BuildStartInfo
{
    [Fact]
    public void AnySolutionPath_StartsCurrentProcessExecutable()
    {
        // Arrange
        string solutionPath = @"C:\projects\MyApp.sln";

        // Act
        System.Diagnostics.ProcessStartInfo result = DaemonProcess.BuildStartInfo(solutionPath);

        // Assert
        result.FileName.ShouldBe(Environment.ProcessPath);
    }

    [Fact]
    public void AnySolutionPath_ArgumentListContainsDaemonFlagAndPath()
    {
        // Arrange
        string solutionPath = @"C:\projects\MyApp.sln";

        // Act
        System.Diagnostics.ProcessStartInfo result = DaemonProcess.BuildStartInfo(solutionPath);

        // Assert
        result.ArgumentList.TakeLast(2).ShouldBe(["--daemon", solutionPath]);
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
        result.ArgumentList[^1].ShouldBe(solutionPath);
    }

    [Fact]
    public void AnySolutionPath_DoesNotRedirectStandardOutput()
    {
        // Arrange
        string solutionPath = @"C:\projects\MyApp.sln";

        // Act
        System.Diagnostics.ProcessStartInfo result = DaemonProcess.BuildStartInfo(solutionPath);

        // Assert
        result.RedirectStandardOutput.ShouldBeFalse();
    }

    [Fact]
    public void AnySolutionPath_DoesNotRedirectStandardError()
    {
        // Arrange
        string solutionPath = @"C:\projects\MyApp.sln";

        // Act
        System.Diagnostics.ProcessStartInfo result = DaemonProcess.BuildStartInfo(solutionPath);

        // Assert
        result.RedirectStandardError.ShouldBeFalse();
    }
}
