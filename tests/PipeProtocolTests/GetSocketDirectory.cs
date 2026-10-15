using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.PipeProtocolTests;

public sealed class GetSocketDirectory : IDisposable
{
    private const string SocketDirEnvVar = "ROSLYN_QUERY_SOCKET_DIR";
    private const string XdgRuntimeDirEnvVar = "XDG_RUNTIME_DIR";

    public GetSocketDirectory()
    {
        // Ensure a clean state at the start of every test
        PipeProtocol.SetSocketDirectoryOverrideForTests(null);
        Environment.SetEnvironmentVariable(SocketDirEnvVar, null);
        Environment.SetEnvironmentVariable(XdgRuntimeDirEnvVar, null);
    }

    [Fact]
    public void WhenOverrideSet_UsesItVerbatim()
    {
        // Arrange
        string expected = Path.Combine(Path.GetTempPath(), "rq-test-socket-override-verbatim");
        PipeProtocol.SetSocketDirectoryOverrideForTests(expected);

        // Act
        string result = PipeProtocol.GetSocketDirectory();

        // Assert
        result.ShouldBe(expected);
    }

    [Fact]
    public void WhenEnvVarSetAndNoOverride_UsesEnvVarVerbatim()
    {
        // Arrange
        string expected = Path.Combine(Path.GetTempPath(), "rq-test-socket-envvar-verbatim");
        Environment.SetEnvironmentVariable(SocketDirEnvVar, expected);

        // Act
        string result = PipeProtocol.GetSocketDirectory();

        // Assert
        result.ShouldBe(expected);
    }

    [Fact]
    public void WhenOverrideAndEnvVarBothSet_OverrideWins()
    {
        // Arrange
        string overrideDir = Path.Combine(Path.GetTempPath(), "rq-test-socket-override-wins");
        string envVarDir = Path.Combine(Path.GetTempPath(), "rq-test-socket-envvar-loses");
        PipeProtocol.SetSocketDirectoryOverrideForTests(overrideDir);
        Environment.SetEnvironmentVariable(SocketDirEnvVar, envVarDir);

        // Act
        string result = PipeProtocol.GetSocketDirectory();

        // Assert
        result.ShouldBe(overrideDir);
    }

    [Fact]
    public void WhenEnvVarAndXdgBothSet_EnvVarWins()
    {
        // Arrange — ROSLYN_QUERY_SOCKET_DIR wins over XDG_RUNTIME_DIR
        string envVarDir = Path.Combine(Path.GetTempPath(), "rq-test-envvar-over-xdg");
        string xdgDir = Path.Combine(Path.GetTempPath(), "rq-test-xdg-loses");
        Environment.SetEnvironmentVariable(SocketDirEnvVar, envVarDir);
        Environment.SetEnvironmentVariable(XdgRuntimeDirEnvVar, xdgDir);

        // Act
        string result = PipeProtocol.GetSocketDirectory();

        // Assert
        result.ShouldBe(envVarDir);
    }

    [Fact]
    public void WhenEnvVarIsRelativePathOnUnix_Throws()
    {
        // Unix-only: a relative socket dir silently falls through to .NET's CoreFxPipe_ branch.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        Environment.SetEnvironmentVariable(SocketDirEnvVar, "relative/path");

        // Act & Assert
        InvalidOperationException ex = Should.Throw<InvalidOperationException>(
            () => PipeProtocol.GetSocketDirectory());
        ex.Message.ShouldContain(SocketDirEnvVar);
        ex.Message.ShouldContain("relative/path");
        ex.Message.ShouldContain("absolute path");
    }

    [Fact]
    public void WhenXdgRuntimeDirSetAndNoOverrideOrEnvVar_UsesXdgSubdir()
    {
        // Arrange
        string xdgDir = Path.Combine(Path.GetTempPath(), "rq-test-xdg-runtime");
        Environment.SetEnvironmentVariable(XdgRuntimeDirEnvVar, xdgDir);

        // Act
        string result = PipeProtocol.GetSocketDirectory();

        // Assert
        result.ShouldBe(Path.Combine(xdgDir, "roslyn-query"));
    }

    [Fact]
    public void WhenXdgEmptyAndNoOverrideOrEnvVar_UsesTmpdirWithRqPrefix()
    {
        // Arrange — constructor cleared XDG_RUNTIME_DIR; confirm fallback fires
        // XDG_RUNTIME_DIR is unset, so GetSocketDirectory must fall through to TMPDIR.

        // Act
        string result = PipeProtocol.GetSocketDirectory();

        // Assert
        result.ShouldStartWith(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
        result.ShouldContain("rq-");
    }

    [Fact]
    public void WhenTmpdirFallback_DoesNotEmbedRawUsername()
    {
        // Arrange — no override, no env var, no XDG; TMPDIR fallback must use hashed username
        // not the raw username, to keep the path short for sun_path on macOS.

        // Act
        string result = PipeProtocol.GetSocketDirectory();

        // Assert — the raw username must not appear verbatim in the TMPDIR fallback path
        string rawUsername = Environment.UserName;
        string dirName = Path.GetFileName(result.TrimEnd(Path.DirectorySeparatorChar));
        dirName.ShouldNotBe($"rq-{rawUsername}", $"Expected the TMPDIR fallback to use a hashed discriminator, not the raw username '{rawUsername}'");

        // The directory name must be exactly 11 chars: "rq-" (3) + 8 hex chars (8)
        dirName.Length.ShouldBe(11);
    }

    public void Dispose()
    {
        PipeProtocol.SetSocketDirectoryOverrideForTests(null);
        Environment.SetEnvironmentVariable(SocketDirEnvVar, null);
        Environment.SetEnvironmentVariable(XdgRuntimeDirEnvVar, null);
    }
}
