namespace ServiceLib.Services;

/// <summary>
/// Runs a lightweight tunnel probe on a fixed cadence. Recovery is requested only
/// after consecutive failures so a single lost packet does not restart a live game.
/// </summary>
public sealed class CoreConnectivityMonitor : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<bool>> _probe;
    private readonly Func<Task> _onFailure;
    private readonly TimeSpan _interval;
    private readonly int _failureThreshold;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _loop;
    private int _consecutiveFailures;

    public CoreConnectivityMonitor(
        Func<CancellationToken, Task<bool>> probe,
        Func<Task> onFailure,
        TimeSpan? interval = null,
        int failureThreshold = 3,
        bool probeImmediately = false)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(onFailure);
        _probe = probe;
        _onFailure = onFailure;
        _interval = interval ?? TimeSpan.FromSeconds(10);
        _failureThreshold = Math.Max(1, failureThreshold);
        _loop = RunAsync(probeImmediately);
    }

    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    public void Stop() => _cancellation.Cancel();

    private async Task RunAsync(bool probeImmediately)
    {
        try
        {
            if (!probeImmediately)
            {
                await Task.Delay(_interval, _cancellation.Token);
            }

            while (!_cancellation.IsCancellationRequested)
            {
                var healthy = false;
                try
                {
                    healthy = await _probe(_cancellation.Token);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("Core connectivity probe failed", ex);
                }

                if (healthy)
                {
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                }
                else if (Interlocked.Increment(ref _consecutiveFailures) >= _failureThreshold)
                {
                    // Stop before invoking recovery. Recovery may stop and recreate
                    // the core, and waiting on this monitor from inside its own loop
                    // would deadlock the restart path.
                    Stop();
                    try
                    {
                        await _onFailure();
                    }
                    catch (Exception ex)
                    {
                        Logging.SaveLog("Core connectivity recovery callback failed", ex);
                    }
                    break;
                }

                await Task.Delay(_interval, _cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }
        _cancellation.Dispose();
    }
}
