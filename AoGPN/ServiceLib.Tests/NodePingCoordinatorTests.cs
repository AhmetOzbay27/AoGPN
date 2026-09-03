namespace ServiceLib.Tests;

public class NodePingCoordinatorTests
{
    [Fact]
    public async Task RunsWithBoundedConcurrencyAndSortsSuccessfulResults()
    {
        var active = 0;
        var peak = 0;
        var progress = new List<NodePingResult>();
        var coordinator = new NodePingCoordinator(2);
        var requests = Enumerable.Range(1, 4).Select(i => new NodePingRequest(i.ToString(), async token =>
        {
            var current = Interlocked.Increment(ref active);
            while (true)
            {
                var observed = Volatile.Read(ref peak);
                if (current <= observed || Interlocked.CompareExchange(ref peak, current, observed) == observed)
                {
                    break;
                }
            }
            await Task.Delay(20, token);
            Interlocked.Decrement(ref active);
            return 100 - i * 10;
        }));

        var results = await coordinator.RunAsync(
            requests,
            TimeSpan.FromSeconds(1),
            progress.Add,
            TestContext.Current.CancellationToken);

        Assert.True(peak <= 2);
        Assert.Equal(4, results.Count);
        Assert.Equal(4, progress.Count);
        Assert.Equal(new[] { "4", "3", "2", "1" }, results.Select(x => x.Id));
    }

    [Fact]
    public async Task ConvertsTimeoutToFailedResult()
    {
        var coordinator = new NodePingCoordinator();
        var results = await coordinator.RunAsync(
            [new NodePingRequest("dead", async token => { await Task.Delay(500, token); return 1; })],
            TimeSpan.FromMilliseconds(20),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(results.Single().IsSuccess);
        Assert.Equal(-1, results.Single().Delay);
    }

    [Fact]
    public async Task PropagatesExternalCancellation()
    {
        using var cts = new CancellationTokenSource(20);
        var coordinator = new NodePingCoordinator();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RunAsync(
            [new NodePingRequest("slow", async token => { await Task.Delay(500, token); return 1; })],
            TimeSpan.FromSeconds(5), cancellationToken: cts.Token));
    }
}
