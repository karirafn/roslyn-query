using System.Collections.Frozen;

using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ReloadStateTests;

public sealed class CompleteReload
{
    private sealed class TrackingDisposable : IDisposable
    {
        public int DisposeCount;
        public void Dispose() => Interlocked.Increment(ref DisposeCount);
    }

    private static WorkspaceLease NewLease() => new(new TrackingDisposable());

    [Fact]
    public void ConcurrentReadWrite_NeverReturnsNullSolution()
    {
        // Arrange
        AdhocWorkspace workspace = new();
        Solution initialSolution = workspace.CurrentSolution;
        ReloadState sut = new(initialSolution, []);
        sut.CompleteInitialLoad(initialSolution, [], FrozenSet<string>.Empty, LoadHealth.Complete, NewLease());

        const int iterations = 1000;
        NullReferenceException? readerException = null;

        // Act
        Thread writer = new(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                sut.TryBeginReload();
                sut.CompleteReload(initialSolution, [], FrozenSet<string>.Empty, LoadHealth.Complete, NewLease());
            }
        });

        Thread reader = new(() =>
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    Solution solution = sut.Solution;
                    solution.ShouldNotBeNull();
                    _ = sut.LastWriteTime;
                }
            }
            catch (NullReferenceException ex)
            {
                readerException = ex;
            }
        });

        writer.Start();
        reader.Start();
        writer.Join();
        reader.Join();

        // Assert
        readerException.ShouldBeNull();
    }

    [Fact]
    public void WhenCompleteReloadCalledWithDocumentPaths_UpdatesDocumentPaths()
    {
        // Arrange
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        ReloadState sut = new(solution, []);
        List<string> pathList = [@"C:\src\Foo.cs"];
        FrozenSet<string> updatedPaths = pathList.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        // Act
        sut.TryBeginReload().ShouldBeTrue();
        sut.CompleteReload(solution, [], updatedPaths, LoadHealth.Complete, NewLease());

        // Assert
        sut.DocumentPaths.ShouldBeSameAs(updatedPaths);
    }
}
