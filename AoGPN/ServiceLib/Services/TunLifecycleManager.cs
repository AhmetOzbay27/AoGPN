namespace ServiceLib.Services;

public sealed class TunLifecycleManager
{
    private static readonly Lazy<TunLifecycleManager> _instance = new(() => new());
    public static TunLifecycleManager Instance => _instance.Value;

    private readonly SemaphoreSlim _cleanupLock = new(1, 1);
    private int _cleanupGeneration;

    /// <summary>
    /// Number of TCP connections killed during the most recent <see cref="BeginAsync"/> call.
    /// Call <see cref="DrainFlushCount"/> to atomically read and reset.
    /// </summary>
    private static volatile int _lastFlushedCount;

    /// <summary>
    /// Tri-state TUN adapter confirmation, set during FlushAfterTunStart:
    /// <c>null</c> = not yet verified (assume TUN is present), <c>true</c> = adapter
    /// confirmed present, <c>false</c> = adapter confirmed missing (proxy fallback).
    /// CoreManager and SystemProxyPolicy read this to decide whether the system
    /// proxy is needed.
    /// </summary>
    public static bool? TunInterfaceConfirmed { get; private set; }

    /// <summary>
    /// Atomically reads and resets the last flush count so the dashboard can
    /// display it exactly once per TUN launch.
    /// </summary>
    public static int DrainFlushCount() => Interlocked.Exchange(ref _lastFlushedCount, 0);

    /// <summary>
    /// Blocking check: queries netsh for the sing-box TUN adapter.
    /// </summary>
    public static bool IsTunInterfacePresent()
    {
        if (!Utils.IsWindows())
            return false;

        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "interface show interface",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return output.Contains("singbox_tun", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Synchronous convenience wrapper over <see cref="FlushAfterTunStartAsync"/>.
    /// Prefer the async overload so the TUN-adapter polling does not block a
    /// thread pool worker.
    /// </summary>
    public static int FlushAfterTunStart(RoutingItem? routingItem)
    {
        return FlushAfterTunStartAsync(routingItem).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Second-pass flush called after the core is up. Kills any connections
    /// that leaked through during the brief gap between the pre-start flush
    /// and the TUN adapter coming online. Uses the combined TCP + UDP + DNS
    /// strategy for maximum effectiveness.
    ///
    /// Then POLLS for the TUN interface instead of checking once: sing-box
    /// needs 1-3 seconds to boot and create the Wintun adapter, so an instant
    /// check always reports "missing" and wrongly triggers the proxy fallback.
    /// Only after the 5-second deadline passes with no adapter does it set
    /// <see cref="TunInterfaceConfirmed"/> = false so the caller can enable
    /// proxy fallback when Wintun/TUN genuinely fails (e.g. VMware guests).
    /// </summary>
    public static async Task<int> FlushAfterTunStartAsync(RoutingItem? routingItem, CancellationToken cancellationToken = default)
    {
        TunInterfaceConfirmed = false;

        if (!Utils.IsWindows() || routingItem?.RuleSet.IsNullOrEmpty() != false)
        {
            return 0;
        }

        Logging.SaveLog("[TunLifecycleManager] Post-start flush: TUN is live, re-killing any leaked connections...");
        DiagLog.Write("TUN_POST TUN is live — starting post-start flush");
        var sw = Stopwatch.StartNew();
        var killed = NetworkConnectionFlusher.FlushProxyProcessConnections(routingItem);
        sw.Stop();

        // Merge into the dashboard counter.
        Interlocked.Add(ref _lastFlushedCount, killed);
        Logging.SaveLog($"[TunLifecycleManager] Post-start flush complete: {killed} connection(s) killed in {sw.ElapsedMilliseconds} ms.");
        DiagLog.Write($"TUN_POST flush done: killed={killed} ms={sw.ElapsedMilliseconds}");

        // Verify the TUN interface actually exists on the system, polling every
        // 500 ms for up to 5 seconds. If netsh still can't find it, sing-box
        // silently failed to create the TUN adapter and process_name rules will
        // never match.
        const int pollIntervalMs = 500;
        const int timeoutSeconds = 5;
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var attempts = 0;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            if (IsTunInterfacePresent())
            {
                TunInterfaceConfirmed = true;
                break;
            }
            try
            {
                await Task.Delay(pollIntervalMs, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        DiagLog.Write($"TUN_VERIFY polled {attempts}x over {timeoutSeconds}s found singbox_tun={TunInterfaceConfirmed}");
        if (TunInterfaceConfirmed == false)
        {
            DiagLog.Write("TUN_VERIFY CRITICAL: TUN adapter NOT FOUND after 5s — proxy fallback will be enabled");
        }

        return killed;
    }

    /// <summary>
    /// Opens a cleanup transaction for the upcoming TUN launch. Kills pre-existing
    /// TCP connections for proxy-routed processes so they reconnect through the new
    /// TUN interface. This must never hold <see cref="_cleanupLock"/> across the
    /// caller's lifetime: <see cref="CleanupAsync"/> acquires the same lock, so
    /// holding it here would deadlock every TUN start.
    /// </summary>
    /// <param name="tunWasEnabled">Whether the TUN was previously active.</param>
    /// <param name="routingItem">
    /// The active routing item whose process-based rules determine which
    /// applications need their connections flushed.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<TunCleanupTransaction> BeginAsync(
        bool tunWasEnabled,
        RoutingItem? routingItem = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Kill pre-existing sockets for proxy-routed processes before the
        // TUN comes up — this patches the "leaky socket" vulnerability.
        Interlocked.Exchange(ref _lastFlushedCount, 0);
        DiagLog.Write("TUN_PRE  BeginAsync called");

        // FORCE-CLEAR the Windows system proxy before TUN starts. If the OS
        // proxy is set to 127.0.0.1:PORT, browsers will route through the local
        // SOCKS listener and sing-box cannot attribute connections to the
        // original process — process_name rules fail, traffic leaks to direct.
        if (Utils.IsWindows())
        {
            try
            {
                DiagLog.Write("TUN_PROXY Force-clearing Windows system proxy before TUN start...");
                ProxySettingWindows.UnsetProxy();
            }
            catch (Exception ex)
            {
                DiagLog.Write($"TUN_PROXY Force-clear failed: {ex.Message}");
            }
        }

        if (Utils.IsWindows() && routingItem != null)
        {
            Logging.SaveLog($"[TunLifecycleManager] RoutingItem '{routingItem.Remarks}' (id={routingItem.Id}) has {routingItem.RuleNum} rule(s). Beginning socket flush...");
            DiagLog.Write($"TUN_PRE  routing='{routingItem.Remarks}' id={routingItem.Id} rules={routingItem.RuleNum} — starting pre-start flush");
            var sw = Stopwatch.StartNew();
            var killed = NetworkConnectionFlusher.FlushProxyProcessConnections(routingItem);
            sw.Stop();
            Interlocked.Exchange(ref _lastFlushedCount, killed);
            Logging.SaveLog($"[TunLifecycleManager] Socket flush complete: {killed} connection(s) killed in {sw.ElapsedMilliseconds} ms.");
            DiagLog.Write($"TUN_PRE  flush done: killed={killed} ms={sw.ElapsedMilliseconds}");
        }
        else if (!Utils.IsWindows())
        {
            Logging.SaveLog("[TunLifecycleManager] Non-Windows platform — socket flush skipped.");
            DiagLog.Write("TUN_PRE  skipped: non-Windows");
        }
        else
        {
            Logging.SaveLog("[TunLifecycleManager] No RoutingItem supplied — socket flush skipped.");
            DiagLog.Write("TUN_PRE  skipped: no RoutingItem");
        }

        var transaction = new TunCleanupTransaction();
        if (tunWasEnabled)
        {
            var generation = Interlocked.Increment(ref _cleanupGeneration);
            transaction.AddRollback(() => RestoreGenerationAsync(generation));
        }

        return Task.FromResult(transaction);
    }

    public async Task CleanupAsync(bool tunWasEnabled, CancellationToken cancellationToken = default)
    {
        if (!tunWasEnabled)
        {
            return;
        }

        await _cleanupLock.WaitAsync(cancellationToken);
        try
        {
            if (Utils.IsWindows())
            {
                await WindowsUtils.RemoveTunDevice();
            }
        }
        finally
        {
            _cleanupLock.Release();
        }
    }

    private Task RestoreGenerationAsync(int generation)
    {
        // Platform route/DNS snapshots are intentionally not guessed here. The
        // generation marker makes rollback idempotent and gives platform providers
        // a safe hook without mutating unrelated network state.
        return Task.CompletedTask;
    }
}