namespace ServiceLib.Services;

public sealed record NodePingRequest(string Id, Func<CancellationToken, Task<int>> Probe);

public sealed record NodePingResult(string Id, int Delay, bool IsSuccess, Exception? Error = null);

public sealed class NodePingCoordinator
{
    private readonly int _maxConcurrency;

    public NodePingCoordinator(int maxConcurrency = 8)
    {
        _maxConcurrency = Math.Max(1, maxConcurrency);
    }

    public async Task<IReadOnlyList<NodePingResult>> RunAsync(
        IEnumerable<NodePingRequest> requests,
        TimeSpan timeout,
        Action<NodePingResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        var items = requests.ToArray();
        var results = new NodePingResult[items.Length];
        using var semaphore = new SemaphoreSlim(_maxConcurrency);
        var progressLock = new object();

        async Task RunOneAsync(NodePingRequest request, int index)
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);
                NodePingResult result;
                try
                {
                    var delay = await request.Probe(timeoutCts.Token).ConfigureAwait(false);
                    result = new(request.Id, delay, delay > 0);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    result = new(request.Id, -1, false);
                }
                catch (Exception ex)
                {
                    result = new(request.Id, -1, false, ex);
                }
                results[index] = result;
                if (progress is not null)
                {
                    lock (progressLock) progress(result);
                }
            }
            finally { semaphore.Release(); }
        }

        await Task.WhenAll(items.Select(RunOneAsync)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return results.OrderBy(result => result.IsSuccess ? result.Delay : int.MaxValue).ThenBy(result => result.Id, StringComparer.Ordinal).ToArray();
    }
}
