using ServiceLib.Models;
using ServiceLib.Services;

namespace ServiceLib.Tests;

public sealed class ConnectionCoordinatorTests
{
    [Fact]
    public async Task SerializesOperationsAndPublishesMonotonicSnapshots()
    {
        var coordinator = new ConnectionCoordinator();
        var snapshots = new List<RuntimeSnapshot>();
        coordinator.SnapshotChanged += snapshots.Add;
        var active = 0;
        var peak = 0;

        async Task Operation(CancellationToken token)
        {
            var current = Interlocked.Increment(ref active);
            peak = Math.Max(peak, current);
            await Task.Delay(20, token);
            Interlocked.Decrement(ref active);
        }

        var results = await Task.WhenAll(
            coordinator.ExecuteAsync(Operation, TestContext.Current.CancellationToken),
            coordinator.ExecuteAsync(Operation, TestContext.Current.CancellationToken));

        Assert.Equal(1, peak);
        Assert.All(results, result => Assert.Equal(ConnectionState.Connected, result.Connection));
        Assert.Equal(snapshots.Count, snapshots.Select(x => x.Sequence).Distinct().Count());
        Assert.True(snapshots.Zip(snapshots.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
    }

    [Fact]
    public async Task CancellationProducesDisconnectedSnapshot()
    {
        var coordinator = new ConnectionCoordinator();
        using var cts = new CancellationTokenSource();
        var task = coordinator.ExecuteAsync(
            async token => await Task.Delay(5000, token),
            cts.Token);
        cts.Cancel();

        var result = await task;

        Assert.Equal(ConnectionState.Disconnected, result.Connection);
        Assert.Equal("OperationCancelled", result.ErrorCode);
    }

    [Fact]
    public async Task FailureProducesFailedSnapshot()
    {
        var coordinator = new ConnectionCoordinator();
        var result = await coordinator.ExecuteAsync(
            _ => throw new InvalidOperationException("boom"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionState.Failed, result.Connection);
        Assert.Equal(nameof(InvalidOperationException), result.ErrorCode);
        Assert.Equal("boom", result.ErrorMessage);
    }
}
