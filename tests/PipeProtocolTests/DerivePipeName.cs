using System.Runtime.InteropServices;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.PipeProtocolTests;

public sealed class DerivePipeName : IDisposable
{
    public DerivePipeName()
    {
        PipeProtocol.SetSocketDirectoryOverrideForTests(null);
    }

    public void Dispose()
    {
        PipeProtocol.SetSocketDirectoryOverrideForTests(null);
    }

    [Fact]
    public void OnUnix_ReturnsRootedPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        string socketDir = Path.Combine(Path.GetTempPath(), $"rq-test-{Guid.NewGuid():N}");
        PipeProtocol.SetSocketDirectoryOverrideForTests(socketDir);

        // Act
        string name = PipeProtocol.DerivePipeName("/tmp/MyApp.sln");

        // Assert
        Path.IsPathRooted(name).ShouldBeTrue();
        name.ShouldStartWith(socketDir);
    }

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
    public void SamePathDifferentCasing_OnCaseInsensitiveOs_ReturnsSameName()
    {
        // Case folding is only applied on case-insensitive file systems (Windows and macOS).
        // On Linux the file system is case-sensitive, so /a/App.sln and /a/APP.sln are
        // distinct files — hashing them to the same pipe name would let a client connect
        // to the wrong daemon.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return;
        }

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
    public void SamePathDifferentCasing_OnLinux_ReturnsDifferentNames()
    {
        // On Linux, /a/App.sln and /a/APP.sln are genuinely distinct files.
        // Hashing them to the same pipe name would let a client attach to the wrong daemon.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        // Arrange — use Unix-style absolute paths that differ only in case
        string lower = "/tmp/projects/myapp/myapp.sln";
        string upper = "/tmp/projects/myapp/MyApp.sln";

        // Act
        string pipeLower = PipeProtocol.DerivePipeName(lower);
        string pipeUpper = PipeProtocol.DerivePipeName(upper);

        // Assert
        pipeLower.ShouldNotBe(pipeUpper);
    }

    [Fact]
    public void SamePathDifferentCasing_OnLinux_PidFilePathsAlsoDiffer()
    {
        // On Linux, /a/App.sln and /a/APP.sln must not map to the same PID file,
        // otherwise a daemon for one solution would be killed by the other's client.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        // Arrange
        string lower = "/tmp/projects/myapp/myapp.sln";
        string upper = "/tmp/projects/myapp/MyApp.sln";

        // Act
        string pidLower = PipeProtocol.DerivePidFilePath(lower);
        string pidUpper = PipeProtocol.DerivePidFilePath(upper);

        // Assert
        pidLower.ShouldNotBe(pidUpper);
    }

    [Fact]
    public void TrailingSeparator_OnLinux_ReturnsSameName()
    {
        // Path.GetFullPath preserves a trailing separator on Linux. Without normalisation,
        // /a/App.sln/ and /a/App.sln hash to different names, causing a client to miss
        // a running daemon and spawn a duplicate.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        // Arrange
        string withSeparator = "/tmp/projects/myapp/myapp.sln/";
        string withoutSeparator = "/tmp/projects/myapp/myapp.sln";

        // Act
        string nameWith = PipeProtocol.DerivePipeName(withSeparator);
        string nameWithout = PipeProtocol.DerivePipeName(withoutSeparator);

        // Assert
        nameWith.ShouldBe(nameWithout);
    }

    [Fact]
    public void TrailingSeparator_OnLinux_PidFilePathsAlsoMatch()
    {
        // Matching PID paths ensure the daemon for a trailing-separator path is found
        // by a client using the canonical path, preventing duplicate daemon spawning.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return;
        }

        // Arrange
        string withSeparator = "/tmp/projects/myapp/myapp.sln/";
        string withoutSeparator = "/tmp/projects/myapp/myapp.sln";

        // Act
        string pidWith = PipeProtocol.DerivePidFilePath(withSeparator);
        string pidWithout = PipeProtocol.DerivePidFilePath(withoutSeparator);

        // Assert
        pidWith.ShouldBe(pidWithout);
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
    public void OnUnix_ResultDoesNotStartWithPrefix()
    {
        // On Unix, DerivePipeName returns a rooted socket path whose filename is the bare
        // 32-hex hash — the roslyn-query- prefix is not part of the socket filename.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        string socketDir = Path.Combine(Path.GetTempPath(), $"rq-test-{Guid.NewGuid():N}");
        PipeProtocol.SetSocketDirectoryOverrideForTests(socketDir);

        // Act
        string name = PipeProtocol.DerivePipeName("/tmp/MyApp.sln");

        // Assert
        Path.GetFileName(name).ShouldNotStartWith("roslyn-query-");
    }

    [Fact]
    public void OnWindows_ResultStartsWithPrefix()
    {
        // On Windows the pipe name is used as a named-pipe identifier, so the human-readable
        // roslyn-query- prefix is meaningful and the shape must not change.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        string path = @"C:\Projects\MyApp\MyApp.sln";

        // Act
        string name = PipeProtocol.DerivePipeName(path);

        // Assert
        name.ShouldStartWith("roslyn-query-");
    }

    [Fact]
    public void AbsolutePath_OnLinux_ProducesRootedPathEndingWithKnownHash()
    {
        // On Unix, DerivePipeName returns a rooted socket path under the per-user socket
        // directory. The socket filename is the bare 32-hex hash of the solution path with
        // no prefix or extension, keeping the path short for sun_path limits.
        //
        // Input: /tmp/MyApp.sln
        // Normalized: /tmp/MyApp.sln (already absolute; case-sensitive Linux → no upper-case)
        // SHA256("/tmp/MyApp.sln")[..32] = 2a9c2fcde95bd699ba7350a208b8e331
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string FixedPath = "/tmp/MyApp.sln";
        const string ExpectedSuffix = "2a9c2fcde95bd699ba7350a208b8e331";
        string socketDir = Path.Combine(Path.GetTempPath(), $"rq-test-known-{Guid.NewGuid():N}");
        PipeProtocol.SetSocketDirectoryOverrideForTests(socketDir);

        // Act
        string name = PipeProtocol.DerivePipeName(FixedPath);

        // Assert
        name.ShouldSatisfyAllConditions(
            () => Path.IsPathRooted(name).ShouldBeTrue(),
            () => name.ShouldStartWith(socketDir),
            () => name.ShouldEndWith(ExpectedSuffix));
    }
}
