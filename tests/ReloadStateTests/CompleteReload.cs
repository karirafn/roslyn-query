using Microsoft.CodeAnalysis;

using RoslynQuery;

using Shouldly;

namespace roslyn_query.Tests.ReloadStateTests;

public sealed class CompleteReload
{
    [Fact]
    public void ConcurrentReadWrite_NeverReturnsNullSolution()
    {
        // Arrange
        AdhocWorkspace workspace = new();
        Solution initialSolution = workspace.CurrentSolution;
        DateTime initialTime = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        ReloadState sut = new(initialSolution, initialTime);

        const int iterations = 1000;
        NullReferenceException? readerException = null;

        // Act
        Thread writer = new(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                sut.CompleteReload(
                    initialSolution,
                    initialTime.AddMinutes(i));
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
}
