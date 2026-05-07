using System.Reflection;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonServerTests;

public sealed class QueryTimeout
{
    [Fact]
    public void QueryTimeoutSeconds_Is60()
    {
        // Arrange
        FieldInfo? field = typeof(DaemonServer).GetField(
            "QueryTimeoutSeconds",
            BindingFlags.NonPublic | BindingFlags.Static);

        // Act
        int? value = (int?)field?.GetValue(null);

        // Assert
        value.ShouldBe(60);
    }
}
