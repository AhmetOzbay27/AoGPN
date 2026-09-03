using ServiceLib.Services;
using ServiceLib.Models;

namespace ServiceLib.Tests;

public sealed class DashboardPublisherTests
{
    [Fact]
    public async Task PublishesOnlyNewerSequences()
    {
        var scripts = new List<string>();
        var publisher = new DashboardPublisher(script => { scripts.Add(script); return Task.CompletedTask; });

        await publisher.PublishAsync(new RuntimeSnapshot { Sequence = 2, Connection = ConnectionState.Connected }, TestContext.Current.CancellationToken);
        await publisher.PublishAsync(new RuntimeSnapshot { Sequence = 1, Connection = ConnectionState.Disconnected }, TestContext.Current.CancellationToken);
        await publisher.PublishAsync(new RuntimeSnapshot { Sequence = 2, Connection = ConnectionState.Failed }, TestContext.Current.CancellationToken);
        await publisher.PublishAsync(new RuntimeSnapshot { Sequence = 3, Connection = ConnectionState.Disconnected }, TestContext.Current.CancellationToken);

        Assert.Equal(2, scripts.Count);
        Assert.Contains("\"Sequence\":2", scripts[0]);
        Assert.Contains("\"Sequence\":3", scripts[1]);
    }
}
