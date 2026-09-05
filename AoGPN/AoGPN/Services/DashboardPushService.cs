using System.Text.Json;
using ServiceLib.Base;
using ServiceLib.Enums;
using ServiceLib.Events;
using ServiceLib.Helper;
using ServiceLib.Models;
using ServiceLib.Services;
using ServiceLib.Services.CoreConfig;
using ServiceLib.Services.Gpn;
using ServiceLib.ViewModels;

namespace AoGPN.Services;

// ─────────────────────────────────────────────────────────────────────────
// DashboardPushService — Egress push kümesi (P0 Faz 2, 3. Dalga)
//
// MainWindow.xaml.cs içindeki dashboard'a giden canlı/olay itmeleri buraya
// taşındı: GPN telemetri (PushGpnTelemetryAsync), yakalama istatistikleri
// (PushGpnCaptureStatsAsync — son anlık görüntü _lastCaptureStats ile burada
// yaşar), PID havuzu köprüsü (GetGpnPidBridge + PushGpnPidPoolAsync),
// WARP/WinDivert sağlık banner'ları, direnç karar günlüğü ve olay itmeleri
// (PushGpnResilienceLogAsync / PushGpnDrainAsync / PushGpnResilienceAsync /
// PushAvailabilityInfoAsync / PushGpnDiagAsync), kural kayması denetimi
// (PushRuleDriftAsync — _lastRuleDriftVerdict durumuyla), süreç kataloğu
// (PushProcessCatalogAsync) ve izleyici anlık görüntüsü
// (PushMonitorSnapshotAsync).
//
// UI kanalına yalnızca ctor'a enjekte edilen delegelerle dokunur: executeScript
// (WebView2 yürütme — MainWindow.ExecuteScriptSafelyAsync), WebViewReady
// bayrağı, ConnectionViewModel / aktif görünüm / transport / gerçek bağlantı
// durumu okuyucuları, WebView2 ömür token'ı ve WARP degrade denetleyicisi
// (GpnBypassEgressController — pencerede WhenActivated'da kurulur, bu yüzden
// tembel okunur). Tray/StatusBar güncellemeleri singleton üzerinden yapılır.
// Dialog/tray/yaşam-döngüsü MainWindow'da kalır. Taşınan gövdelerde hiçbir
// satır değişmedi — yalnızca yukarıdaki pencere üyelerine yapılan çağrılar
// ilgili delegeye mekanik olarak yönlendirildi.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// Dashboard egress push'ları (telemetri/direnç/izleyici/kayma) iş mantığı.
/// MainWindow tarafından kurulur; IDashboardBridge push üyeleri bu servise
/// delege edilir.
/// </summary>
internal sealed class DashboardPushService
{
    private readonly Func<string, Task> _executeScript;
    private readonly Func<bool> _isWebViewReady;
    private readonly Func<SplitTunnelViewModel?> _getConnectionViewModel;
    private readonly Func<string> _readTransport;
    private readonly Func<bool> _readActualConnectionState;
    private readonly Func<string> _getActiveView;
    private readonly Func<CancellationToken> _getWebViewToken;
    private readonly Func<GpnBypassEgressController?> _getBypassEgressController;

    // Route-test results are pushed to the renderer with camelCase keys to match the
    // rest of the host→renderer payloads.
    private static readonly JsonSerializerOptions RouteTestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly GpnTelemetryService _gpnTelemetry = new();
    private readonly GpnResilienceLog _gpnResilienceLog = new();
    private readonly ProcessCatalogService _processCatalogService = new();

    /// <summary>GpnCaptureLoop'un son yayınladığı telemetri anlık görüntüsü (dashboard'a yeniden basmak için).</summary>
    private GpnCaptureStatsSnapshot? _lastCaptureStats;

    // GPN PID havuzu köprüsü — SplitTunnelViewModel'in vpn eylemli oyunlarından
    // GpnTargetResolver'ı canlı çalıştırır; dashboard "PID havuzu" kartını besler.
    private GpnTargetResolverBridge? _gpnPidBridge;

    // Last rule-drift verdict pushed to the dashboard ("InSync"/"Drifted"/...).
    // The periodic health check only republishes when the verdict changes, so the
    // banner never flickers on every 30 s tick.
    private string _lastRuleDriftVerdict = "";

    public DashboardPushService(
        Func<string, Task> executeScript,
        Func<bool> isWebViewReady,
        Func<SplitTunnelViewModel?> getConnectionViewModel,
        Func<string> readTransport,
        Func<bool> readActualConnectionState,
        Func<string> getActiveView,
        Func<CancellationToken> getWebViewToken,
        Func<GpnBypassEgressController?> getBypassEgressController)
    {
        _executeScript = executeScript ?? throw new ArgumentNullException(nameof(executeScript));
        _isWebViewReady = isWebViewReady ?? throw new ArgumentNullException(nameof(isWebViewReady));
        _getConnectionViewModel = getConnectionViewModel ?? throw new ArgumentNullException(nameof(getConnectionViewModel));
        _readTransport = readTransport ?? throw new ArgumentNullException(nameof(readTransport));
        _readActualConnectionState = readActualConnectionState ?? throw new ArgumentNullException(nameof(readActualConnectionState));
        _getActiveView = getActiveView ?? throw new ArgumentNullException(nameof(getActiveView));
        _getWebViewToken = getWebViewToken ?? throw new ArgumentNullException(nameof(getWebViewToken));
        _getBypassEgressController = getBypassEgressController ?? throw new ArgumentNullException(nameof(getBypassEgressController));
    }

    private Task ExecuteScriptSafelyAsync(string script) => _executeScript(script);
    private bool WebViewReady => _isWebViewReady();

    /// <summary>Lazily-created GPN PID pool bridge (subscribe/snapshot source for the dashboard).</summary>
    public GpnTargetResolverBridge GpnPidBridge => GetGpnPidBridge();

    /// <summary>GpnCaptureLoop son anlık görüntüsünü kaydeder (canlı akış aboneliği bu servisi besler).</summary>
    public void UpdateLastCaptureStats(GpnCaptureStatsSnapshot snap) => _lastCaptureStats = snap;

    public void ResetGpnTelemetry() => _gpnTelemetry.Reset();
    public void ClearGpnResilienceLog() => _gpnResilienceLog.Clear();

    /// <summary>Pencere kapanırken PID havuzu köprüsünün olay aboneliğini bırakır.</summary>
    public void DisposePidBridge() => _gpnPidBridge?.Dispose();

    public bool TryResolveExecutablePath(int pid, out string runningPath)
        => _processCatalogService.TryResolveExecutablePath(pid, out runningPath);

    /// <summary>
    /// Pushes a GPN diagnostic line (GPN_LOG / GPN_RECOVER / GPN_SELECT ...) into
    /// the dashboard diagnostics feed. The event is published by DiagLog for every
    /// "GPN_*" write, so the feed mirrors ao_diag.txt without re-reading the file.
    /// </summary>
    public async Task PushGpnDiagAsync(GpnDiagEvent evt)
    {
        if (!WebViewReady)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(evt, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setGpnDiag?.({json});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-diag push failed", ex);
        }
    }

    public async Task PushGpnTelemetryAsync()
    {
        if (!WebViewReady)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(_gpnTelemetry.Snapshot, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setGpnTelemetry?.({json});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-telemetry push failed", ex);
        }
    }

    /// <summary>
    /// GpnCaptureLoop'un paket başına telemetrisini dashboard'a taşır
    /// (protokol/yön/byte sayaçları, top akışlar, per-PID atfı). Canlı akış
    /// AppEvents.GpnCaptureStatsChanged'den gelir; bu yöntem son görüntüyü
    /// dashboard açılışı / get_gpn_telemetry / failover olaylarında yeniden basar.
    /// </summary>
    public async Task PushGpnCaptureStatsAsync()
    {
        if (!WebViewReady)
        {
            return;
        }

        try
        {
            if (_lastCaptureStats is not { } snap)
            {
                return; // döngü hiç çalışmadı — kart boş kalır
            }
            var json = JsonSerializer.Serialize(snap, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setGpnCaptureStats?.({json});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-capture-stats push failed", ex);
        }
    }

    /// <summary>
    /// SplitTunnelViewModel'e bağlı köprü: Game Boost'taki vpn eylemli oyunların
    /// exe adlarından GpnTargetResolver'ı canlı (5 sn) çalıştırır. Olayların
    /// dashboard'a taşınması için tek abonelik kurulur.
    /// </summary>
    private GpnTargetResolverBridge GetGpnPidBridge()
    {
        if (_gpnPidBridge is null)
        {
            _gpnPidBridge = new GpnTargetResolverBridge(_getConnectionViewModel()!);
            _gpnPidBridge.SnapshotChanged += async _ => await PushGpnPidPoolAsync();
        }
        return _gpnPidBridge;
    }

    /// <summary>PID havuzu anlık görüntüsünü dashboard'a gönderir (özel veri taşınmaz).</summary>
    public async Task PushGpnPidPoolAsync()
    {
        if (!WebViewReady)
        {
            return;
        }

        try
        {
            var bridge = _gpnPidBridge;
            if (bridge?.LastSnapshot is not { } snap)
            {
                return;
            }
            var json = JsonSerializer.Serialize(
                new
                {
                    targetNames = snap.TargetNames,
                    pids = snap.Pids,
                    version = snap.Version,
                    resolvedAt = snap.ResolvedAt.ToUnixTimeMilliseconds(),
                    watching = snap.Watching,
                    targetRunning = snap.TargetRunning,
                    sourceStatus = snap.SourceStatus.ToString(),
                },
                RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setGpnPidPool?.({json});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-pid-pool push failed", ex);
        }
    }

    /// <summary>
    /// WARP dial sağlığını dashboard'a gönderir. `window.setWarpHealth` banner'ı
    /// gösterir/gizler; veri yalnızca faulted bayrağı, hata sayısı ve son hata
    /// metnidir (ağ geçidi adresi veya anahtar gibi hassas veri taşınmaz).
    /// </summary>
    public async Task PushWarpHealthAsync()
    {
        var health = WarpDialHealthMonitor.Instance.Snapshot;
        // Degrade durumu GpnBypassEgressController'dan gelir: faulted iken launcher
        // egress'i DIRECT'e çekilmişse dashboard rozetini gösterir (yalnızca bayrak —
        // hassas veri taşınmaz).
        var degraded = _getBypassEgressController()?.Degraded ?? false;
        var json = JsonSerializer.Serialize(
            new
            {
                health.Faulted,
                health.ErrorCount,
                health.LatestError,
                Degraded = degraded,
            },
            RouteTestJsonOptions);
        await ExecuteScriptSafelyAsync($"window.setWarpHealth?.({json});");
    }

    public async Task PushWinDivertHealthAsync()
    {
        var health = WinDivertHealthMonitor.Instance.Snapshot;
        var json = JsonSerializer.Serialize(
            new
            {
                State = health.State.ToString(),
                health.Message,
                health.NativeError,
            },
            RouteTestJsonOptions);
        await ExecuteScriptSafelyAsync($"window.setWinDivertHealth?.({json});");
    }

    public async Task PushGpnResilienceLogAsync()
    {
        if (!WebViewReady)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(
                new { entries = _gpnResilienceLog.Recent, path = _gpnResilienceLog.LogPath },
                RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setGpnResilienceLog?.({json});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-resilience-log push failed", ex);
        }
    }

    /// <summary>
    /// Pushes a soft node-switch drain snapshot (old node draining after a
    /// restart-free switch) into the dashboard status line, in camelCase.
    /// </summary>
    public async Task PushGpnDrainAsync(GpnDrainSnapshot snap)
    {
        if (!WebViewReady)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(snap, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setGpnNodeSwitch?.({json});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-drain push failed", ex);
        }
    }

    /// <summary>
    /// Pushes a GPN resilience decision (server switch, UDP death, mode fallback to
    /// V2rayTCP, or Tier-2 recovery) into the dashboard. The event carries no traffic;
    /// it only surfaces what GpnServerSelectionService already decided, in camelCase.
    /// </summary>
    public async Task PushGpnResilienceAsync(GpnResilienceEvent evt)
    {
        if (!WebViewReady)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(evt, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setGpnResilience?.({json});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-resilience push failed", ex);
        }

        // Her failover/kurtarma olayından sonra telemetri sayaçlarını, yakalanan
        // trafik istatistiklerini ve döngüsel karar günlüğünü de tazele.
        await PushGpnTelemetryAsync();
        await PushGpnCaptureStatsAsync();
        await PushGpnResilienceLogAsync();

        // Bağlan sırasında seçilen sunucuyu + gecikmeyi + modu StatusBar'a ve
        // dashboard telemetri panelinin Ping kartına / oturum düğümüne canlı taşı.
        // ModeDecision aday seçiminin sonucudur; sunucu adı ve gecikme mevcutsa
        // hem tepsi satırını hem telemetriyi güncelle (ağ çağrısı tekrarlanmaz).
        if (evt.Action is GpnResilienceAction.ModeDecision
            && evt.ServerName.IsNotEmpty())
        {
            var status = StatusBarViewModel.Instance;
            var mode = evt.ToMode == ConnectionMode.V2rayTCP ? "V2rayTCP" : "WireGuard";
            var latency = evt.DelayMs is >= 0 ? $"{evt.DelayMs} ms" : "—";
            status.RunningServerDisplay = $"{evt.ServerName} · {latency} · {mode}";
            status.TrayStatusLine = $"GPN → {evt.ServerName} ({latency}, {mode})";
            status.TrayStatusState = 2;

            try
            {
                var infoJson = JsonSerializer.Serialize(new
                {
                    Server = evt.ServerName,
                    DelayMs = evt.DelayMs,
                    Mode = mode,
                }, RouteTestJsonOptions);
                await ExecuteScriptSafelyAsync($"window.setGpnConnectionInfo?.({infoJson});");
            }
            catch (Exception ex)
            {
                Logging.SaveLog("AoGPN gpn-connection-info push failed", ex);
            }
        }
    }

    /// <summary>
    /// Sunucu kullanılabilirlik ölçümü sonucunu (StatusBarViewModel.TestServerAvailability
    /// → ConnectionHandler.RunAvailabilityCheckData) dashboard ana paneline taşır:
    /// gecikme Ping kartına, IP + sunucu adı alt satırına yazılır. Bağlantı kurulduktan
    /// sonra otomatik ölçüm (RunAvailabilityCheckAfterConnectAsync) ve manuel ⚡ Test
    /// butonu bu akıştan beslenir — ölçüm yalnızca WPF durum çubuğunda kalmaz.
    /// </summary>
    public async Task PushAvailabilityInfoAsync(AvailabilityCheckResult result)
    {
        if (!WebViewReady)
        {
            return;
        }

        try
        {
            var infoJson = JsonSerializer.Serialize(new
            {
                Server = result.ServerName,
                DelayMs = result.DelayMs,
                Ip = result.Ip,
                Country = result.Country,
            }, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setAvailabilityInfo?.({infoJson});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN availability-info push failed", ex);
        }
    }

    /// <summary>
    /// Runs the rule-drift health check on a background thread and pushes the verdict
    /// into the dashboard. The check rebuilds the sing-box config the active core would
    /// use right now and compares its route rules against the config the running core
    /// loaded — a mismatch means the tunnel is enforcing stale rules (e.g. routing edited
    /// in the settings without a reload). No traffic is sent.
    /// </summary>
    public async Task PushRuleDriftAsync(bool force = false)
    {
        if (!WebViewReady)
        {
            return;
        }

        try
        {
            var config = AppManager.Instance.Config;
            var report = await Task.Run(async () => await new RoutingDriftHealthCheck(config).CheckAsync());

            var verdict = report.IsDrifted ? "drifted" : report.State.ToString();
            var previousVerdict = Volatile.Read(ref _lastRuleDriftVerdict);
            if (!force && verdict == previousVerdict)
            {
                return;
            }
            Volatile.Write(ref _lastRuleDriftVerdict, verdict);

            AppEvents.RuleDriftChanged.Publish(report);
            if (report.IsDrifted)
            {
                DiagLog.Write($"RULE_DRIFT UI routing={report.ActiveRoutingId} missing={report.MissingRules.Count} extra={report.ExtraRules.Count} reordered={report.Reordered}");
            }

            var json = JsonSerializer.Serialize(report, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setRuleDrift({json});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN rule-drift check failed", ex);
        }
    }

    public async Task PushProcessCatalogAsync()
    {
        if (!WebViewReady)
        {
            return;
        }

        try
        {
            var processes = await Task.Run(() => _processCatalogService.GetRunningProcesses(_getWebViewToken()));
            var payload = processes.Select(item => new
            {
                pid = item.Pid,
                processName = item.ProcessName,
                displayName = item.DisplayName,
                exePath = item.ExePath,
                isElevatedProcess = item.IsElevatedProcess,
            });
            await ExecuteScriptSafelyAsync(
                $"window.updateProcessList({JsonSerializer.Serialize(payload)});");
        }
        catch (OperationCanceledException) when (_getWebViewToken().IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN process catalog push failed", ex);
        }
    }

    public async Task PushMonitorSnapshotAsync(bool force = false)
    {
        if (!WebViewReady || _getConnectionViewModel() is not { } connectionViewModel)
        {
            return;
        }

        // The connection/split tables only exist on the Performance and Game Boost
        // views. The 2 s poll skips serialization and DOM re-rendering while the user
        // is elsewhere; entering those views requests a snapshot explicitly.
        if (!force && _getActiveView() is not ("perf" or "boost"))
        {
            return;
        }

        var monitor = connectionViewModel.Monitor;
        var connections = monitor.Connections
            .Take(300)
            .Select(item => new
            {
                processName = item.ProcessName,
                displayName = item.DisplayName,
                exePath = item.ExePath,
                pid = item.Pid,
                protocol = item.Protocol,
                remoteAddress = item.RemoteAddress,
                localAddress = item.LocalAddress,
                state = item.State,
                routeTag = item.RouteTag,
                routeText = item.RouteText,
                countryText = item.CountryText,
                asnText = item.AsnText,
            })
            .ToList();

        // Propagate the active-node ping to every running app so the dashboard
        // boost cards show per-game latency from the real telemetry loop.
        var activePing = connectionViewModel.Telemetry.PingValue;
        foreach (var app in connectionViewModel.Apps)
        {
            if (app.IsRunning && activePing > 0)
            {
                app.LatencyMs = activePing;
                app.LatencyText = activePing + " ms";
            }
            else
            {
                // Clear latency for idle/paused apps so old values don't linger.
                app.LatencyMs = -1;
                app.LatencyText = "—";
            }
        }

        var apps = connectionViewModel.Apps
            .Take(200)
            .Select(item => new
            {
                processName = item.ProcessName,
                displayName = item.DisplayName,
                entryType = item.EntryType,
                value = item.Value,
                action = item.Action,
                routeTag = item.RouteTag,
                routeText = item.RouteText,
                isRunning = item.IsRunning,
                runStatusText = item.RunStatusText,
                liveRouteTag = item.LiveRouteTag,
                liveRouteText = item.LiveRouteText,
                liveConnectionCount = item.LiveConnectionCount,
                downloadText = item.DownloadText,
                uploadText = item.UploadText,
                activeIps = item.ActiveIps,
                needsTun = item.NeedsTun,
                exeMissing = item.ExeMissing,
                latencyMs = item.LatencyMs,
                latencyText = item.LatencyText,
                beforePingMs = item.BeforePingMs,
                beforePingText = item.BeforePingText,
                afterPingMs = item.AfterPingMs,
                afterPingText = item.AfterPingText,
                pingDeltaText = item.PingDeltaText,
            })
            .ToList();

        var traffic = monitor.AppTrafficItems
            .Take(100)
            .Select(item => new
            {
                appName = item.AppName,
                exePath = item.ExePath,
                downloadText = item.DownloadText,
                uploadText = item.UploadText,
                activeIps = item.ActiveIps,
                connectionCount = item.ConnectionCount,
            })
            .ToList();

        var mode = connectionViewModel.Mode switch
        {
            SplitTunnelViewModel.ModeManual => "manual",
            SplitTunnelViewModel.ModeVpn => "vpn",
            _ => "off",
        };
        var transport = _readTransport();
        var flushedCount = TunLifecycleManager.DrainFlushCount();

        // Compute the routing-engine mode the core is running under.
        //  - gpn   : GPN Game Tunnel — stripped v2rayN baggage, process_name only
        //  - global: Global VPN — full legacy clash_mode / geoip / hosts chain
        //  - proxy : Proxy capture — no TUN, OS proxy routes all traffic
        //  - none  : Disconnected or undefined
        string routingMode;
        if (!_readActualConnectionState())
        {
            routingMode = "none";
        }
        else
        {
            routingMode = transport switch
            {
                "tun" when mode == "manual" => "gpn",
                "tun" when mode == "vpn" => "global",
                "tun" => "global",
                "proxy" => "proxy",
                _ => "none",
            };
        }

        // Yakalama sürüklenmesi (A2): GPN modunda tünellenmesi gereken bir
        // uygulamanın canlı bağlantısı var ama köprü onun PID'lerinden hiç paket
        // saymadıysa trafik doğrudan gidiyordur — ping düşmez, hiçbir hata
        // görünmez. Yalnızca köprünün beklendiği modda (tun + manual = "gpn")
        // denetle: global/proxy modunda yakalama zaten hedef değildir, sürüklenme
        // kavramı yoktur. Boş PID kümesi (kimliksiz sahip) yargılanmaz.
        object[] captureDrift = [];
        if (routingMode == "gpn")
        {
            var livePidsByProcess = monitor.Connections
                .Where(c => c.Pid > 0 && c.ProcessName.IsNotEmpty())
                .GroupBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyCollection<int>)g.Select(c => c.Pid).Distinct().ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            var candidates = connectionViewModel.Apps
                .Where(a => a.EntryType == "app"
                    && string.Equals(a.Action, connectionViewModel.InvertManualRouting ? "direct" : "vpn", StringComparison.OrdinalIgnoreCase)
                    && a.LiveConnectionCount > 0)
                .Select(a => new CaptureDriftCandidate(
                    a.Value,
                    a.LiveConnectionCount,
                    livePidsByProcess.TryGetValue(a.Value, out var pids) ? pids : []));
            captureDrift = GpnCaptureDriftChecker.FindDrifted(_lastCaptureStats?.ByPid, candidates)
                .Select(d => new { processName = d.ProcessName, liveConnections = d.LiveConnections })
                .ToArray();
        }

        var payload = new
        {
            updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            connected = _readActualConnectionState(),
            mode,
            transport,
            invertManualRouting = connectionViewModel.InvertManualRouting,
            autoConnectOnGameStart = connectionViewModel.AutoConnectOnGameStart,
            activeConnectionCount = monitor.ActiveConnectionCount,
            activeAppCount = monitor.ActiveAppCount,
            activeCountryCount = monitor.ActiveCountryCount,
            totalDownloadText = monitor.TotalDownloadText,
            totalUploadText = monitor.TotalUploadText,
            trafficStatus = monitor.TrafficStatus,
            flushedSocketCount = flushedCount,
            routingMode,
            captureDrift,
            connections,
            apps,
            traffic,
        };

        var json = JsonSerializer.Serialize(payload);
        await ExecuteScriptSafelyAsync($"window.updateMonitorSnapshot({json});");
    }
}
