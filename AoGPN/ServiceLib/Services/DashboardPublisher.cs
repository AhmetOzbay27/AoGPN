using System.Text.Json;
using ServiceLib.Models;

namespace ServiceLib.Services;

public sealed class DashboardPublisher
{
    private readonly Func<string, Task> _executeScriptAsync;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _lastSequence;

    public DashboardPublisher(Func<string, Task> executeScriptAsync)
    {
        _executeScriptAsync = executeScriptAsync ?? throw new ArgumentNullException(nameof(executeScriptAsync));
    }

    public async Task PublishAsync(RuntimeSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (snapshot.Sequence <= _lastSequence) return;
            _lastSequence = snapshot.Sequence;
            var json = JsonSerializer.Serialize(snapshot);
            await _executeScriptAsync($"window.setRuntimeSnapshot({json});").ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
