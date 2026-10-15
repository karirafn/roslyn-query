using Shouldly;

namespace roslyn_query.Tests.DaemonIntegrationTests;

/// <summary>
/// Real-apphost integration test: two client processes query the same daemon concurrently.
/// Both must succeed (exit 0), proving that the concurrent-accept loop serves multiple
/// connections from one snapshot.
/// </summary>
public sealed class ConcurrentQueries : IAsyncLifetime
{
    private const int DeadlineSeconds = 120;

    private readonly DaemonTestFixture _fixture = new("concurrent");

    public async Task InitializeAsync()
    {
        if (_fixture.Skip)
        {
            return;
        }

        // Copy the fixture to a temp dir so the solution path (and derived pipe name) is unique.
        _fixture.CopyFixture();

        // Ensure cold start.
        await _fixture.StopDaemonAsync();
    }

    public async Task DisposeAsync()
    {
        if (_fixture.Skip)
        {
            return;
        }

        await _fixture.StopDaemonAsync();
        _fixture.Cleanup();
    }

    [Fact]
    public async Task WhenTwoClientsQueryConcurrently_BothSucceedFromOneDaemon()
    {
        // Arrange
        if (_fixture.Skip)
        {
            // Graceful skip when binary absent — consistent with ADR 0002.
            return;
        }

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(DeadlineSeconds));

        // Warm up: run one command to ensure the daemon is loaded and ready.
        int warmupExit = await _fixture.RunClientAsync(
            ["list-projects", _fixture.FixtureSlnxPath], cts.Token);
        warmupExit.ShouldBe(0, "warm-up list-projects should succeed");

        // Act — start two client processes concurrently against the same running daemon.
        Task<int> client1 = _fixture.RunClientAsync(
            ["list-projects", _fixture.FixtureSlnxPath], cts.Token);
        Task<int> client2 = _fixture.RunClientAsync(
            ["list-projects", _fixture.FixtureSlnxPath], cts.Token);

        int[] results = await Task.WhenAll(client1, client2);

        // Assert — both clients succeed, proving concurrent serving.
        results[0].ShouldBe(0, "first concurrent client should exit 0");
        results[1].ShouldBe(0, "second concurrent client should exit 0");
    }
}
