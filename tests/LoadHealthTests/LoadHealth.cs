using Shouldly;

namespace roslyn_query.Tests.LoadHealthTests;

public sealed class LoadHealth
{
    [Fact]
    public void WhenComplete_FailureCountIsZero()
    {
        // Arrange / Act
        RoslynQuery.LoadHealth health = RoslynQuery.LoadHealth.Complete;

        // Assert
        health.FailureCount.ShouldBe(0);
    }

    [Fact]
    public void WhenComplete_IsPartialIsFalse()
    {
        // Arrange / Act
        RoslynQuery.LoadHealth health = RoslynQuery.LoadHealth.Complete;

        // Assert
        health.IsPartial.ShouldBeFalse();
    }

    [Fact]
    public void WhenComplete_FailureMessagesIsEmpty()
    {
        // Arrange / Act
        RoslynQuery.LoadHealth health = RoslynQuery.LoadHealth.Complete;

        // Assert
        health.FailureMessages.ShouldBeEmpty();
    }

    [Fact]
    public void WhenConstructedWithMessages_FailureCountMatchesMessageCount()
    {
        // Arrange / Act
        RoslynQuery.LoadHealth health = new(["x", "y"]);

        // Assert
        health.FailureCount.ShouldBe(2);
    }

    [Fact]
    public void WhenConstructedWithMessages_IsPartialIsTrue()
    {
        // Arrange / Act
        RoslynQuery.LoadHealth health = new(["x", "y"]);

        // Assert
        health.IsPartial.ShouldBeTrue();
    }

    [Fact]
    public void WhenConstructedWithMessages_ExposesProvidedMessages()
    {
        // Arrange
        string[] messages = ["x", "y"];

        // Act
        RoslynQuery.LoadHealth health = new(messages);

        // Assert
        health.FailureMessages.ShouldBe(messages);
    }

    [Fact]
    public void WhenConstructedWithNullMessages_ThrowsArgumentNullException()
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentNullException>(() => new RoslynQuery.LoadHealth(null!));
    }
}
