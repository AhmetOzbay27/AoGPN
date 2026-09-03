namespace ServiceLib.Services;

public sealed class TunCleanupTransaction : IAsyncDisposable
{
    private readonly List<Func<Task>> _rollbackActions = [];
    private bool _completed;
    private bool _rolledBack;

    public bool IsCompleted => _completed;
    public bool IsRolledBack => _rolledBack;

    public void AddRollback(Func<Task> rollback)
    {
        ArgumentNullException.ThrowIfNull(rollback);
        if (_completed || _rolledBack)
        {
            throw new InvalidOperationException("The TUN cleanup transaction is no longer active.");
        }
        _rollbackActions.Add(rollback);
    }

    public async Task CompleteAsync()
    {
        if (_completed || _rolledBack)
        {
            return;
        }
        _completed = true;
        _rollbackActions.Clear();
        await Task.CompletedTask;
    }

    public async Task RollbackAsync()
    {
        if (_completed || _rolledBack)
        {
            return;
        }

        _rolledBack = true;
        for (var index = _rollbackActions.Count - 1; index >= 0; index--)
        {
            try
            {
                await _rollbackActions[index]();
            }
            catch (Exception ex)
            {
                Logging.SaveLog("TunCleanupTransaction", ex);
            }
        }
        _rollbackActions.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed && !_rolledBack)
        {
            await RollbackAsync();
        }
    }
}
