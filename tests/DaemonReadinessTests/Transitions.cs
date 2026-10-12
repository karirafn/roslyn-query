using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.DaemonReadinessTests;

public sealed class Transitions
{
    [Fact]
    public void InitialState_IsLoading()
    {
        // Arrange / Act
        DaemonReadiness sut = new();

        // Assert
        sut.State.ShouldBe(DaemonReadinessState.Loading);
    }

    [Fact]
    public void WhenTransitionedToReady_StateIsReady()
    {
        // Arrange
        DaemonReadiness sut = new();

        // Act
        sut.TransitionToReady();

        // Assert
        sut.State.ShouldBe(DaemonReadinessState.Ready);
    }

    [Fact]
    public void WhenTransitionedReadyThenReloading_StateIsReloading()
    {
        // Arrange
        DaemonReadiness sut = new();
        sut.TransitionToReady();

        // Act
        sut.TransitionToReloading();

        // Assert
        sut.State.ShouldBe(DaemonReadinessState.Reloading);
    }

    [Fact]
    public void WhenTransitionedReloadingThenReady_StateIsReady()
    {
        // Arrange
        DaemonReadiness sut = new();
        sut.TransitionToReady();
        sut.TransitionToReloading();

        // Act
        sut.TransitionToReady();

        // Assert
        sut.State.ShouldBe(DaemonReadinessState.Ready);
    }

    [Fact]
    public void WhenReadFromMultipleThreads_ReturnsSameState()
    {
        // Arrange
        DaemonReadiness sut = new();
        sut.TransitionToReady();
        List<DaemonReadinessState> results = new(100);
        object resultsLock = new();

        // Act
        Parallel.For(0, 100, _ =>
        {
            DaemonReadinessState state = sut.State;
            lock (resultsLock)
            {
                results.Add(state);
            }
        });

        // Assert
        results.ShouldAllBe(s => s == DaemonReadinessState.Ready);
    }
}
