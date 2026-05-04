using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ReloadStateTests;

public sealed class TryBeginReload
{
    [Fact]
    public void TwoSimultaneousCalls_ExactlyOneReturnsTrue()
    {
        // Arrange
        AdhocWorkspace workspace = new();
        Solution initialSolution = workspace.CurrentSolution;
        ReloadState sut = new(initialSolution, []);

        using ManualResetEventSlim barrier = new(false);
        bool result1 = false;
        bool result2 = false;

        // Act
        Thread t1 = new(() =>
        {
            barrier.Wait();
            result1 = sut.TryBeginReload();
        });

        Thread t2 = new(() =>
        {
            barrier.Wait();
            result2 = sut.TryBeginReload();
        });

        t1.Start();
        t2.Start();
        barrier.Set();
        t1.Join();
        t2.Join();

        // Assert
        int trueCount = (result1 ? 1 : 0) + (result2 ? 1 : 0);
        trueCount.ShouldBe(1);
    }
}
