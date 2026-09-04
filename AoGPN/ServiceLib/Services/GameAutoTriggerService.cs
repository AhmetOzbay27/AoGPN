namespace ServiceLib.Services;

/// <summary>
/// Oyun otomatik tetikleyicisinin arka plan mantığı (P0 Faz 3): GPN için eklenmiş
/// oyunların çalışıp çalışmadığını izleyen süreç taramasının kısıt (throttle) durumunu
/// ve tetikleyici kararını sahiplenir. Saf durum makinesi (<see cref="GameTriggerStateMachine"/>)
/// bu servisin içinde yaşar; ürettiği kararlar çağıran tarafça (SplitTunnelViewModel —
/// bağlı reaktif durumun ve uygulamanın tek sahibi) uygulanır.
///
/// Servis tarama temposunu kendisi kurmaz: tarama yalnızca pencere görünürken ve
/// (Manuel modda ya da tetikleyici açıkken Off modunda) çalışır — bu koşullar görünüm
/// durumudur, bu yüzden sürücü (3 saniyelik canlı durum döngüsü) ViewModel'de kalır ve
/// her tikte <see cref="TryBeginScan"/> + <see cref="ScanRunningProcessesAsync"/> ile
/// bu servise girer. Servis, süreç taramasını kendi içinde Task.Run ile yürütür.
/// </summary>
public sealed class GameAutoTriggerService
{
    private readonly GameTriggerStateMachine _gameTrigger = new();

    private DateTime _lastProcessScan = DateTime.MinValue;

    public bool ShouldScanProcesses(int mode, bool autoConnect)
    {
        if (!AppManager.Instance.ShowInTaskbar)
        {
            return false;
        }
        if (mode != GameTriggerModes.Manual && !(mode == GameTriggerModes.Off && autoConnect))
        {
            return false;
        }
        return (DateTime.UtcNow - _lastProcessScan).TotalMilliseconds >= 1000;
    }

    /// <summary>
    /// True when the per-process scan should run: the window is visible and either the
    /// app is in Manuel mode, or it is Off with the game auto-connect trigger enabled
    /// (the trigger needs the scan to detect a game starting). A minimum interval
    /// avoids burst scans when the monitor refreshes frequently.
    /// </summary>
    public bool TryBeginScan(int mode, bool autoConnect)
    {
        if (!ShouldScanProcesses(mode, autoConnect))
        {
            return false;
        }
        _lastProcessScan = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Checks only the listed process names instead of enumerating the whole system,
    /// so the periodic scan stays cheap. Runs on the thread pool (never blocks the
    /// UI thread); the caller drives the cadence.
    /// </summary>
    public Task<HashSet<string>> ScanRunningProcessesAsync(IReadOnlyCollection<string> processNames)
    {
        return Task.Run(() => GetRunningProcessNames(processNames));
    }

    /// <summary>True once the trigger switched to Manuel mode and the restore is pending.</summary>
    public bool TriggerActive => _gameTrigger.TriggerActive;

    /// <summary>
    /// Evaluates one running-game snapshot through the pure state machine. The caller
    /// (SplitTunnelViewModel) applies the returned decision to its bound state.
    /// </summary>
    public GameTriggerDecision Tick(bool enabled, int currentMode, IReadOnlyCollection<string> runningVpnApps)
    {
        return _gameTrigger.Tick(enabled, currentMode, runningVpnApps);
    }

    /// <summary>Forgets the pending restore (user changed the mode, disabled the trigger or pressed İptal).</summary>
    public void Reset()
    {
        _gameTrigger.Reset();
    }

    private static HashSet<string> GetRunningProcessNames(IReadOnlyCollection<string> processNames)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in processNames)
        {
            if (name.IsNullOrEmpty())
            {
                continue;
            }
            try
            {
                var processes = Process.GetProcessesByName(name);
                if (processes.Length > 0)
                {
                    set.Add(name);
                }
                foreach (var p in processes)
                {
                    try
                    {
                        p.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
                // Invalid process name or access denied — treat as not running.
            }
        }
        return set;
    }
}
