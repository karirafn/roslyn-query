using System.Diagnostics;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonProcessTests;

public sealed class IsDaemonProcess
{
    private static readonly string ApphostName =
        typeof(DaemonProcess).Assembly.GetName().Name!;

    // IsDaemonProcessCore tests — drive matching logic directly via the injectable seam

    [Fact]
    public void WhenModuleNameMatchesApphost_ReturnsTrue()
    {
        // Arrange
        string moduleName = ApphostName;

        // Act
        bool result = DaemonProcess.IsDaemonProcessCore(moduleName);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void WhenModuleNameMatchesApphostWithExe_ReturnsTrue()
    {
        // Arrange
        string moduleName = ApphostName + ".exe";

        // Act
        bool result = DaemonProcess.IsDaemonProcessCore(moduleName);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void WhenModuleNameMatchesApphostCaseInsensitive_ReturnsTrue()
    {
        // Arrange
        string moduleName = ApphostName.ToUpperInvariant();

        // Act
        bool result = DaemonProcess.IsDaemonProcessCore(moduleName);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void WhenModuleNameIsDotnet_ReturnsTrue()
    {
        // Arrange
        string moduleName = "dotnet";

        // Act
        bool result = DaemonProcess.IsDaemonProcessCore(moduleName);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void WhenModuleNameIsDotnetExe_ReturnsTrue()
    {
        // Arrange
        string moduleName = "dotnet.exe";

        // Act
        bool result = DaemonProcess.IsDaemonProcessCore(moduleName);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void WhenModuleNameIsNull_ReturnsFalse()
    {
        // Arrange / Act
        bool result = DaemonProcess.IsDaemonProcessCore(null);

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public void WhenModuleNameIsUnrelated_ReturnsFalse()
    {
        // Arrange
        string moduleName = "notepad.exe";

        // Act
        bool result = DaemonProcess.IsDaemonProcessCore(moduleName);

        // Assert
        result.ShouldBeFalse();
    }

    // IsDaemonProcess(Process) public surface — exercises the fail-closed exception filter

    [Fact]
    public void WhenPassedCurrentProcess_ReturnsBooleanWithoutThrowing()
    {
        // Arrange
        using Process current = Process.GetCurrentProcess();

        // Act
        bool result = DaemonProcess.IsDaemonProcess(current);

        // Assert — the test host is either dotnet or testhost; either branch returns a bool,
        // never throws. We assert only that the call completes — the specific value depends on
        // the runtime, and the core logic is covered by the IsDaemonProcessCore tests above.
        result.ShouldBeOneOf(true, false);
    }
}
