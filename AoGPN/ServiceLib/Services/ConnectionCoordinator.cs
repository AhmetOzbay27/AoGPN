using ServiceLib.Models;

namespace ServiceLib.Services;

public sealed class ConnectionCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private CancellationTokenSource? _operationCancellation;
    private long _sequence;

    public RuntimeSnapshot Snapshot { get; private set; } = new();
    public event Action<RuntimeSnapshot>? SnapshotChanged;

    public async Task<RuntimeSnapshot> ExecuteAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _operationCancellation = linked;
        try
        {
            Publish(Snapshot with { Connection = ConnectionState.Preparing, ErrorCode = null, ErrorMessage = null });
            await operation(linked.Token).ConfigureAwait(false);
            return Publish(Snapshot with { Connection = ConnectionState.Connected });
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return Publish(Snapshot with { Connection = ConnectionState.Disconnected, ErrorCode = "OperationCancelled", ErrorMessage = null });
        }
        catch (Exception ex)
        {
            return Publish(Snapshot with { Connection = ConnectionState.Failed, ErrorCode = ex.GetType().Name, ErrorMessage = ex.Message });
        }
        finally
        {
            if (ReferenceEquals(_operationCancellation, linked))
            {
                _operationCancellation = null;
            }
            _gate.Release();
        }
    }

    public void Cancel() => _operationCancellation?.Cancel();

    public RuntimeSnapshot SetState(ConnectionState state, string? nodeId = null, string? mode = null, string? transport = null)
        => Publish(Snapshot with
        {
            Connection = state,
            ActiveNodeId = nodeId ?? Snapshot.ActiveNodeId,
            Mode = mode ?? Snapshot.Mode,
            Transport = transport ?? Snapshot.Transport,
        });

    private RuntimeSnapshot Publish(RuntimeSnapshot snapshot)
    {
        RuntimeSnapshot next;
        lock (_stateLock)
        {
            next = snapshot with { Sequence = ++_sequence, UpdatedAt = DateTimeOffset.UtcNow };
            Snapshot = next;
        }
        SnapshotChanged?.Invoke(next);
        return next;
    }
}
