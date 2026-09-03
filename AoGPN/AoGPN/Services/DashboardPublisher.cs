using ServiceLib.Models;

namespace AoGPN.Services;

public sealed class DashboardPublisher
{
    private readonly ServiceLib.Services.DashboardPublisher _inner;

    public DashboardPublisher(Func<string, Task> executeScriptAsync)
    {
        _inner = new ServiceLib.Services.DashboardPublisher(executeScriptAsync);
    }

    public Task PublishAsync(RuntimeSnapshot snapshot, CancellationToken cancellationToken = default)
        => _inner.PublishAsync(snapshot, cancellationToken);
}
