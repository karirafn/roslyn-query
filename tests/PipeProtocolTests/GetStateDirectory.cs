using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.PipeProtocolTests;

public sealed class GetStateDirectory : IDisposable
{
    private const string EnvVar = "ROSLYN_QUERY_STATE_DIR";

    public GetStateDirectory()
    {
        // Ensure a clean state at the start of every test
        PipeProtocol.SetStateDirectoryOverrideForTests(null);
        Environment.SetEnvironmentVariable(EnvVar, null);
    }

    [Fact]
    public void WhenOverrideSet_UsesItVerbatim()
    {
        // Arrange
        string expected = Path.Combine(Path.GetTempPath(), "rq-test-override-verbatim");
        PipeProtocol.SetStateDirectoryOverrideForTests(expected);

        // Act
        string result = PipeProtocol.GetStateDirectory();

        // Assert
        result.ShouldBe(expected);
    }

    [Fact]
    public void WhenEnvVarSetAndNoOverride_UsesEnvVarVerbatim()
    {
        // Arrange
        string expected = Path.Combine(Path.GetTempPath(), "rq-test-envvar-verbatim");
        Environment.SetEnvironmentVariable(EnvVar, expected);

        // Act
        string result = PipeProtocol.GetStateDirectory();

        // Assert
        result.ShouldBe(expected);
    }

    [Fact]
    public void WhenOverrideAndEnvVarBothSet_OverrideWins()
    {
        // Arrange
        string overrideDir = Path.Combine(Path.GetTempPath(), "rq-test-override-wins");
        string envVarDir = Path.Combine(Path.GetTempPath(), "rq-test-envvar-loses");
        PipeProtocol.SetStateDirectoryOverrideForTests(overrideDir);
        Environment.SetEnvironmentVariable(EnvVar, envVarDir);

        // Act
        string result = PipeProtocol.GetStateDirectory();

        // Assert
        result.ShouldBe(overrideDir);
    }

    [Fact]
    public void WhenNeitherSetAndLocalAppDataPresent_UsesLocalAppDataSubfolder()
    {
        // Arrange — both cleared in constructor

        // Act
        string result = PipeProtocol.GetStateDirectory();

        // Assert — this environment has HOME set so LocalApplicationData is non-empty
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "roslyn-query");
        result.ShouldBe(expected);
    }

    public void Dispose()
    {
        PipeProtocol.SetStateDirectoryOverrideForTests(null);
        Environment.SetEnvironmentVariable(EnvVar, null);
    }
}
