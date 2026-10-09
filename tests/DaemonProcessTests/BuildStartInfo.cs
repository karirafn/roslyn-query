using System.Diagnostics;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonProcessTests;

public sealed class BuildStartInfo
{
    [Fact]
    public void ApphostPath_FileNameIsPathAndArgumentListIsDaemonAndSolution()
    {
        // Arrange
        string solutionPath = @"C:\projects\MyApp.sln";
        string processPath = "/usr/local/bin/roslyn-query";
        string assemblyLocation = "/app/roslyn-query.dll";

        // Act
        ProcessStartInfo result = DaemonProcess.BuildStartInfoCore(solutionPath, processPath, assemblyLocation);

        // Assert
        result.ShouldSatisfyAllConditions(
            () => result.FileName.ShouldBe(processPath),
            () => result.ArgumentList.ShouldBe(["--daemon", solutionPath]));
    }

    [Fact]
    public void DotnetHost_PrependsAssemblyLocation()
    {
        // Arrange
        string solutionPath = @"C:\projects\MyApp.sln";
        string processPath = "/usr/bin/dotnet";
        string assemblyLocation = "/app/roslyn-query.dll";

        // Act
        ProcessStartInfo result = DaemonProcess.BuildStartInfoCore(solutionPath, processPath, assemblyLocation);

        // Assert
        result.ShouldSatisfyAllConditions(
            () => result.FileName.ShouldBe(processPath),
            () => result.ArgumentList.ShouldBe([assemblyLocation, "--daemon", solutionPath]));
    }

    [Fact]
    public void DotnetHostWindowsCasing_PrependsAssemblyLocation()
    {
        // Path.GetFileNameWithoutExtension uses the OS path separator — backslash is only a
        // separator on Windows, so a Windows-style path is identified as the dotnet host only there.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        string solutionPath = @"C:\projects\MyApp.sln";
        string processPath = @"C:\Program Files\dotnet\dotnet.exe";
        string assemblyLocation = @"C:\app\roslyn-query.dll";

        // Act
        ProcessStartInfo result = DaemonProcess.BuildStartInfoCore(solutionPath, processPath, assemblyLocation);

        // Assert
        result.ShouldSatisfyAllConditions(
            () => result.FileName.ShouldBe(processPath),
            () => result.ArgumentList.ShouldBe([assemblyLocation, "--daemon", solutionPath]));
    }

    [Fact]
    public void NullProcessPath_Throws()
    {
        // Arrange
        string solutionPath = @"C:\projects\MyApp.sln";
        string assemblyLocation = "/app/roslyn-query.dll";

        // Act / Assert
        Should.Throw<InvalidOperationException>(
            () => DaemonProcess.BuildStartInfoCore(solutionPath, null, assemblyLocation));
    }

    [Fact]
    public void PathContainingDoubleQuote_ArgumentListPreservesRawPath()
    {
        // Arrange
        string solutionPath = @"C:\proj\my""evil.sln";
        string processPath = "/usr/local/bin/roslyn-query";
        string assemblyLocation = "/app/roslyn-query.dll";

        // Act
        ProcessStartInfo result = DaemonProcess.BuildStartInfoCore(solutionPath, processPath, assemblyLocation);

        // Assert
        result.ArgumentList[^1].ShouldBe(solutionPath);
    }

    [Fact]
    public void AnySolutionPath_DoesNotRedirectStandardOutput()
    {
        // Arrange
        string solutionPath = @"C:\projects\MyApp.sln";
        string processPath = "/usr/local/bin/roslyn-query";
        string assemblyLocation = "/app/roslyn-query.dll";

        // Act
        ProcessStartInfo result = DaemonProcess.BuildStartInfoCore(solutionPath, processPath, assemblyLocation);

        // Assert
        result.RedirectStandardOutput.ShouldBeFalse();
    }

    [Fact]
    public void AnySolutionPath_DoesNotRedirectStandardError()
    {
        // Arrange
        string solutionPath = @"C:\projects\MyApp.sln";
        string processPath = "/usr/local/bin/roslyn-query";
        string assemblyLocation = "/app/roslyn-query.dll";

        // Act
        ProcessStartInfo result = DaemonProcess.BuildStartInfoCore(solutionPath, processPath, assemblyLocation);

        // Assert
        result.RedirectStandardError.ShouldBeFalse();
    }
}
