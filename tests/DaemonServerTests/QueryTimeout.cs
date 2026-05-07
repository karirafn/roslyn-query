using Shouldly;

namespace roslyn_query.Tests.DaemonServerTests;

public sealed class QueryTimeout
{
    // Regression guard — if the timeout value changes, this test fails and forces a deliberate review.
    // The constant is private on DaemonServer; 60 seconds is the expected production value.
    private const int ExpectedQueryTimeoutSeconds = 60;

    [Fact]
    public void QueryTimeoutSeconds_Is60()
    {
        // Arrange
        System.Reflection.FieldInfo? field = typeof(RoslynQuery.DaemonServer).GetField(
            "QueryTimeoutSeconds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        // Act
        int? value = (int?)field?.GetValue(null);

        // Assert — this is a deliberate regression guard; update ExpectedQueryTimeoutSeconds if changing the timeout
        value.ShouldBe(ExpectedQueryTimeoutSeconds);
    }
}
