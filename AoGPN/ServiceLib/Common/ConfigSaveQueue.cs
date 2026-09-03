namespace ServiceLib.Common;

/// <summary>
/// Serializes config writes. Requests are coalesced (only the latest state is kept),
/// writes never run concurrently, and <see cref="FlushAsync"/> / <see cref="SaveAndWaitAsync"/>
/// guarantee everything is on disk before proceeding — used before applying rules and
/// on app exit so no queued save is ever lost.
/// </summary>
public static class ConfigSaveQueue
{
    private static readonly object _sync = new();
    private static readonly Queue<Config> _queue = new();
    private static bool _writing;
    private static TaskCompletionSource? _idleSignal;

    /// <summary>Test seam: the actual write action. Defaults to the config handler.</summary>
    public static Func<Config, Task<int>> SaveAction { get; set; } = ConfigHandler.SaveConfig;

    /// <summary>
    /// Requests a save. Repeated rapid requests collapse into a single write of the
    /// newest config — intermediate states never hit the disk.
    /// </summary>
    public static void RequestSave(Config config)
    {
        lock (_sync)
        {
            _queue.Clear(); // debounce: only the latest state matters
            _queue.Enqueue(config);
            if (_writing)
            {
                return;
            }
            _writing = true;
            _idleSignal = null;
            _ = WriteLoopAsync();
        }
    }

    /// <summary>Requests a save and waits until it (and everything queued before it) is on disk.</summary>
    public static async Task SaveAndWaitAsync(Config config)
    {
        RequestSave(config);
        // ConfigureAwait(false): see WaitForFlushAsync. Callers may block on this
        // synchronously from the UI thread during exit; the continuation must not
        // need the UI dispatcher to run.
        await FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// How long <see cref="FlushAsync"/> waits for an in-flight write before
    /// giving up. A stuck write (e.g. the config file locked by a second
    /// instance) previously blocked every SaveAndWaitAsync caller forever —
    /// including the exit path's state flush. Test seam: lowered in tests.
    /// </summary>
    internal static TimeSpan FlushTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public static Task FlushAsync()
    {
        TaskCompletionSource signal;
        lock (_sync)
        {
            if (!_writing)
            {
                return Task.CompletedTask;
            }
            _idleSignal ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            signal = _idleSignal;
        }

        return WaitForFlushAsync(signal);
    }

    private static async Task WaitForFlushAsync(TaskCompletionSource signal)
    {
        try
        {
            // ConfigureAwait(false): the flush completion must never depend on the
            // calling thread's SynchronizationContext. OnExit calls
            // SaveAndWaitAsync(...).GetAwaiter().GetResult() on the UI thread while
            // the UI thread is blocked — if the in-flight write yields and its
            // continuation (or this timeout handler) is posted back to the UI
            // dispatcher, the wait never completes and shutdown deadlocks forever
            // (observed live on 2026-09-03: OnExit entered, nothing happened for
            // 70 s until the exit watchdog force-killed the process).
            await signal.Task.WaitAsync(FlushTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Logging.SaveLog($"ConfigSaveQueue flush timed out after {FlushTimeout.TotalSeconds}s — "
                + "continuing without waiting for the queued write.");
        }
    }

    private static async Task WriteLoopAsync()
    {
        while (true)
        {
            Config? config;
            lock (_sync)
            {
                if (_queue.Count == 0)
                {
                    _writing = false;
                    _idleSignal?.TrySetResult();
                    return;
                }
                config = _queue.Dequeue();
            }

            try
            {
                // ConfigureAwait(false) — see WaitForFlushAsync: the write loop must
                // make progress even when the caller is synchronously blocked on a
                // UI-thread SaveAndWaitAsync during exit.
                await SaveAction(config).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logging.SaveLog("ConfigSaveQueue", ex);
            }
        }
    }
}
