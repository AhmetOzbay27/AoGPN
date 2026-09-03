using System.Collections.Specialized;
using System.ComponentModel;
using System.Reactive.Concurrency;
using ServiceLib.Services.CoreConfig.Mihomo;
using ServiceLib.Services.Gpn;

namespace ServiceLib.ViewModels;

public class SplitTunnelViewModel : MyReactiveObject
{
    public Interaction<Unit, string?> BrowseExeInteraction { get; } = new();

    /// <summary>Reads the clipboard text (registered by the view, WPF-only).</summary>
    public Interaction<Unit, string?> GetClipboardTextInteraction { get; } = new();

    public IObservableCollection<SplitTunnelAppItem> Apps { get; } = new ObservableCollectionExtended<SplitTunnelAppItem>();

    /// <summary>Apps filtered by the live search box (name / value / type / connection).</summary>
    public IObservableCollection<SplitTunnelAppItem> FilteredApps { get; } = new ObservableCollectionExtended<SplitTunnelAppItem>();

    /// <summary>
    /// Live connection monitoring, merged into the same page so the user can see what
    /// every app goes through (proxy / direct / block) while managing its route.
    /// </summary>
    public ConnectionMonitorViewModel Monitor { get; } = new();

    /// <summary>Real-time ping / loss / speed telemetry for the dashboard strip.</summary>
    public TelemetryDashboardViewModel Telemetry { get; } = new();

    /// <summary>Connection choices for each manual list entry.</summary>
    public List<ComboItem> ManualActions { get; } = new();

    [Reactive]
    public SplitTunnelAppItem? SelectedApp { get; set; }

    [Reactive]
    public string DomainInput { get; set; } = "";

    /// <summary>Live filter for the manual list (matches name, value, type and connection).</summary>
    [Reactive]
    public string ManualFilter { get; set; } = "";

    [Reactive]
    public string ActiveRoutingRemarks { get; set; }

    // Connection modes: Off / VPN / Manuel (values shared with the game trigger)
    public const int ModeOff = GameTriggerModes.Off;
    public const int ModeVpn = GameTriggerModes.Vpn;
    public const int ModeManual = GameTriggerModes.Manual;

    [Reactive]
    public int Mode { get; set; }

    /// <summary>
    /// GPN routing direction. False = whitelist (only listed apps tunneled).
    /// True = blacklist (listed apps stay direct, everything else tunnels).
    /// </summary>
    [Reactive]
    public bool InvertManualRouting { get; set; }

    /// <summary>
    /// Capture transport override selected in the dashboard: "tun" (full network
    /// capture), "proxy" (system proxy), or "" to derive from the routing mode.
    /// Mirrors AoGPN's EnableTun + SysProxyType controls and lets Global VPN run
    /// over the system proxy without administrator privileges.
    /// </summary>
    [Reactive]
    public string Transport { get; set; } = "";

    [Reactive]
    public bool IsManual { get; set; }

    [Reactive]
    public bool IsVpn { get; set; }

    [Reactive]
    public string StatusText { get; set; } = "";

    [Reactive]
    public bool NeedAdmin { get; set; }

    /// <summary>True when at least one listed app bypasses the system proxy (TUN needed).</summary>
    [Reactive]
    public bool AnyNeedsTun { get; set; }

    /// <summary>
    /// When enabled, a listed game with a VPN route auto-switches the app to Manuel
    /// mode (and applies the rules) as soon as it starts running, then restores the
    /// previous mode when the last such game closes.
    /// </summary>
    [Reactive]
    public bool AutoConnectOnGameStart { get; set; }

    /// <summary>True while the game trigger is active and a restore to the previous mode is pending.</summary>
    [Reactive]
    public bool TriggerPending { get; set; }

    /// <summary>Banner text naming the games that keep the pending restore active.</summary>
    [Reactive]
    public string TriggerPendingText { get; set; } = "";

    public ReactiveCommand<Unit, Unit> RefreshCmd { get; }
    public ReactiveCommand<Unit, Unit> AddAppCmd { get; }
    public ReactiveCommand<Unit, Unit> AddDomainCmd { get; }
    public ReactiveCommand<Unit, Unit> EditCmd { get; }
    public ReactiveCommand<Unit, Unit> ImportClipboardCmd { get; }
    public ReactiveCommand<Unit, Unit> RemoveAppCmd { get; }
    public ReactiveCommand<Unit, Unit> ApplyCmd { get; }
    public ReactiveCommand<Unit, Unit> RebootAsAdminCmd { get; }
    public ReactiveCommand<Unit, Unit> CancelTriggerCmd { get; }

    private RoutingItem? _routingItem;

    private CancellationTokenSource? _autoApplyCts;
    private bool _autoApplyRunning;
    private bool _suppressAutoApply;
    private bool _batchUpdating;
    private bool _initialized;

    // Game-trigger state: set when the trigger switches the mode itself (so the mode
    // subscription does not treat it as a user change and cancel the trigger). The
    // trigger decisions themselves live in the pure GameTriggerStateMachine.
    private bool _settingModeProgrammatically;
    private readonly GameTriggerStateMachine _gameTrigger = new();
    private DateTime _lastProcessScan = DateTime.MinValue;

    /// <summary>Son GPN bağlantı modu (WireGuardUDP / V2rayTCP). WARP uyarısı buna göre verilir.</summary>
    private ConnectionMode? _activeGpnMode;

    // GPN uygulamalarının gerçek sunucu uç noktalarını kaydeden mağaza ("gerçek
    // ping" ölçümü): bağlantı monitöründe görülen uzak adresler buraya beslenir,
    // program açılışında (önce) ve GPN bağlantısından sonra (sonra) ölçülür.
    private readonly GpnAppEndpointStore _endpointStore = GpnAppEndpointStore.Instance;

    public SplitTunnelViewModel()
    {
        // Periyodik flush döngüsünü başlat (idempotent) — gözlemler diske düzenli
        // yazılır ve eski uç noktalar budanır.
        _endpointStore.Start();

        _config = AppManager.Instance.Config;

        // Three real connection options. "proxy" and "vpn+proxy" were merged into
        // "vpn": they all map to the same proxy outbound, the capture transport
        // decides proxy vs TUN globally, and legacy values are normalised on load.
        ManualActions.AddRange(new[]
        {
            new ComboItem { ID = "vpn", Text = ResUI.ManualActionVpn },
            new ComboItem { ID = "direct", Text = ResUI.ManualActionDirect },
            new ComboItem { ID = "block", Text = ResUI.ManualActionBlock },
            new ComboItem { ID = "warp", Text = ResUI.ManualActionWarp },
        });

        _config.ConnectionItem ??= new();
        _config.ConnectionItem.ManualRoutes ??= [];
        Mode = _config.ConnectionItem.Mode;
        InvertManualRouting = _config.ConnectionItem.InvertManualRouting;
        Transport = _config.ConnectionItem.Transport is "tun" or "proxy"
            ? _config.ConnectionItem.Transport
            : "";
        AutoConnectOnGameStart = _config.ConnectionItem.AutoConnectOnGameStart;
        // Persist the trigger toggle immediately (fire-and-forget, flushed on exit).
        this.WhenAnyValue(x => x.AutoConnectOnGameStart)
            .Subscribe(enabled =>
            {
                _config.ConnectionItem ??= new();
                _config.ConnectionItem.AutoConnectOnGameStart = enabled;
                ConfigSaveQueue.RequestSave(_config);
                if (!enabled)
                {
                    CancelGameTrigger();
                }
            });

        RefreshCmd = ReactiveCommand.CreateFromTask(async () => await LoadAsync());
        AddAppCmd = ReactiveCommand.CreateFromTask(async () => await AddAppAsync());
        AddDomainCmd = ReactiveCommand.CreateFromTask(async () => await AddDomainAsync());
        EditCmd = ReactiveCommand.CreateFromTask(async () => await EditSelectedAppAsync());
        ImportClipboardCmd = ReactiveCommand.CreateFromTask(async () => await ImportClipboardAsync());
        RemoveAppCmd = ReactiveCommand.Create(RemoveSelectedApp);
        ApplyCmd = ReactiveCommand.CreateFromTask(async () => await ApplyAsync());
        RebootAsAdminCmd = ReactiveCommand.CreateFromTask(async () => await AppManager.Instance.RebootAsAdmin());
        CancelTriggerCmd = ReactiveCommand.Create(CancelGameTriggerFromUi);

        IsManual = Mode == ModeManual;
        IsVpn = Mode == ModeVpn;
        // Any mode change applies immediately (no Uygula needed). Guarded by
        // _initialized so restoring the saved mode at startup does not reload the core.
        // A mode change the trigger performs itself must not cancel the trigger.
        this.WhenAnyValue(x => x.Mode)
            .Subscribe(m =>
            {
                IsManual = m == ModeManual;
                IsVpn = m == ModeVpn;
                if (_settingModeProgrammatically)
                {
                    _settingModeProgrammatically = false;
                }
                else
                {
                    // The user (or another flow) took over the mode: forget the restore.
                    CancelGameTrigger();
                }
                OnModeChanged();
            });

        // Keep the per-app traffic columns in sync with the live monitor.
        Monitor.AppTrafficItems.CollectionChanged += (_, _) => ApplyTrafficToApps();
        Apps.CollectionChanged += (_, _) => ApplyTrafficToApps();
        Apps.CollectionChanged += OnAppsCollectionChanged;

        // GPN bağlantı modunu takip et: WARP rotası yalnızca WireGuard modunda
        // çalışır; V2ray TCP (fallback) modunda WARP kuralları sessizce VPN'e düşer —
        // kullanıcı bu durumda açık bir uyarı görmeli.
        AppEvents.GpnConnectionStateChanged.AsObservable().Subscribe(snap =>
        {
            _activeGpnMode = snap.Mode;
            if (snap.State == GpnConnectionState.Connected
                && snap.Mode == ConnectionMode.V2rayTCP
                && Apps.Any(a => a.Action == "warp"))
            {
                StatusText = ResUI.ManualActionWarpNeedsWg;
                NoticeManager.Instance.Enqueue(ResUI.ManualActionWarpNeedsWg);
            }
        });

        // Rebuild the filtered view when the search box changes.
        this.WhenAnyValue(x => x.ManualFilter)
            .Subscribe(_ => RebuildFilteredApps());

        // Keep the running-status and live-connection columns in sync.
        Monitor.Connections.CollectionChanged += (_, _) => _ = RefreshLiveStatusAsync();
        _ = StartLiveStatusLoopAsync();

        _ = LoadAsync();
    }

    /// <summary>Hooks per-item property changes so a combo change auto-applies the list.</summary>
    private void OnAppsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<SplitTunnelAppItem>())
            {
                item.PropertyChanged -= Item_PropertyChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<SplitTunnelAppItem>())
            {
                item.PropertyChanged += Item_PropertyChanged;
            }
        }
        OnListChanged();
        RebuildFilteredApps();
    }

    /// <summary>Repopulates the filtered view from the source list and the current filter.</summary>
    private void RebuildFilteredApps()
    {
        var filter = ManualFilter?.Trim() ?? "";
        FilteredApps.Clear();
        foreach (var app in Apps)
        {
            if (MatchesFilter(app, filter))
            {
                FilteredApps.Add(app);
            }
        }
    }

    /// <summary>Matches an entry against the live filter: display name, value, type and connection option.</summary>
    private bool MatchesFilter(SplitTunnelAppItem app, string filter)
    {
        if (filter.IsNullOrEmpty())
        {
            return true;
        }
        var actionText = ManualActions.FirstOrDefault(x => x.ID == app.Action)?.Text ?? app.Action;
        var haystack = $"{app.DisplayName} {app.ValueText} {app.TypeText} {actionText}";
        return haystack.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var isAction = e.PropertyName == nameof(SplitTunnelAppItem.Action);
        var isFilterRelevant = isAction
            || e.PropertyName == nameof(SplitTunnelAppItem.DisplayName)
            || e.PropertyName == nameof(SplitTunnelAppItem.Value);
        if (!isFilterRelevant)
        {
            return;
        }
        if (isAction && sender is SplitTunnelAppItem item)
        {
            item.UpdateSuggested(); // hide the "Önerildi" badge once the option deviates
        }
        if (isAction)
        {
            OnListChanged();
        }
        RebuildFilteredApps();
    }

    /// <summary>
    /// Called whenever the manual list changes (add / remove / connection option).
    /// Persists the list so it survives a restart, updates the configured-route badge
    /// instantly and, in Manuel mode, schedules a debounced auto-apply so the rules hit
    /// the core without pressing Uygula.
    /// </summary>
    private void OnListChanged()
    {
        if (_suppressAutoApply || _batchUpdating)
        {
            return;
        }

        // Keep the config in sync on every mutation (any mode); the app also flushes
        // the config on exit, so the list is restored with the same settings next launch.
        SyncManualRoutesToConfig();
        ConfigSaveQueue.RequestSave(_config);

        foreach (var app in Apps)
        {
            app.RouteTag = MapActionToOutbound(app.Action);
            app.RouteText = RouteText(app.RouteTag);
        }

        if (Mode != ModeManual)
        {
            return;
        }

        ScheduleAutoApply();
    }

    /// <summary>Copies the current list into the config model so it is persisted.</summary>
    private void SyncManualRoutesToConfig()
    {
        _config.ConnectionItem ??= new();
        _config.ConnectionItem.ManualRoutes = Apps
            .Where(a => a.Value.IsNotEmpty())
            .Select(a => new ManualRouteSetting
            {
                EntryType = a.EntryType,
                Value = a.Value,
                Port = a.Port,
                DisplayName = a.DisplayName,
                ExePath = a.ExePath,
                Action = a.Action,
                SuggestedAction = a.SuggestedAction,
            })
            .ToList();
    }

    /// <summary>Auto-applies the active mode after switching modes (any mode).</summary>
    private void OnModeChanged()
    {
        Logging.Verbose("GPN", "mode_changed",
            ("mode", Mode), ("transport", Transport), ("isManual", IsManual), ("isVpn", IsVpn),
            ("triggerActive", _gameTrigger.TriggerActive));

        if (!_initialized || _suppressAutoApply)
        {
            return;
        }
        ScheduleAutoApply();
    }

    private void ScheduleAutoApply()
    {
        _autoApplyCts?.Cancel();
        _autoApplyCts = new CancellationTokenSource();
        var token = _autoApplyCts.Token;
        _ = DebouncedAutoApplyAsync(token);
    }

    private async Task DebouncedAutoApplyAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(800), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (token.IsCancellationRequested || _autoApplyRunning)
        {
            return;
        }

        _autoApplyRunning = true;
        try
        {
            await ApplyAsync(true);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("SplitTunnelViewModel.AutoApply", ex);
        }
        finally
        {
            _autoApplyRunning = false;
        }

        Logging.Verbose("GPN", "auto_apply", $"mode={Mode} transport={Transport}");
    }

    private async Task StartLiveStatusLoopAsync()
    {
        while (true)
        {
            try
            {
                await RefreshLiveStatusAsync();
            }
            catch (Exception ex)
            {
                Logging.SaveLog("SplitTunnelViewModel.LiveStatusLoop", ex);
            }

            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    /// <summary>
    /// Refreshes the per-row running status and the live connection route. The process
    /// scan only runs when it matters — window visible and Manuel mode (or Off with the
    /// game trigger enabled); the connection-based columns always refresh.
    /// </summary>
    private async Task RefreshLiveStatusAsync()
    {
        HashSet<string>? running = null;
        if (ShouldScanProcesses())
        {
            _lastProcessScan = DateTime.UtcNow;
            var names = Apps
                .Where(a => a.EntryType == "app")
                .Select(a => Path.GetFileNameWithoutExtension(a.Value))
                .Where(n => n.IsNotEmpty())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            running = await Task.Run(() => GetRunningProcessNames(names));
        }

        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            UpdateLiveStatus(running);
            CheckGameTrigger(running);
        });
    }

    /// <summary>
    /// True when the per-process scan should run: the window is visible and either the
    /// app is in Manuel mode, or it is Off with the game auto-connect trigger enabled
    /// (the trigger needs the scan to detect a game starting). A minimum interval
    /// avoids burst scans when the monitor refreshes frequently.
    /// </summary>
    private bool ShouldScanProcesses()
    {
        if (!AppManager.Instance.ShowInTaskbar)
        {
            return false;
        }
        if (Mode != ModeManual && !(Mode == ModeOff && AutoConnectOnGameStart))
        {
            return false;
        }
        return (DateTime.UtcNow - _lastProcessScan).TotalMilliseconds >= 1000;
    }

    /// <summary>
    /// Auto-connect trigger: when a listed VPN-routed game starts running while the
    /// mode is Off, switch to Manuel (which auto-applies the rules). When the last
    /// such game closes, restore the previous mode. A manual mode change by the user
    /// cancels the trigger so the app never fights the user. The decisions are made
    /// by the pure GameTriggerStateMachine; this method only applies them.
    /// </summary>
    private void CheckGameTrigger(HashSet<string>? running)
    {
        if (running is null)
        {
            return; // process scan gated off — nothing to evaluate
        }

        var runningVpnApps = Apps
            .Where(a => a.EntryType == "app" && a.Action is "vpn" or "vpn+proxy" or "warp")
            .Select(a => Path.GetFileNameWithoutExtension(a.Value))
            .Where(p => p.IsNotEmpty())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        runningVpnApps.IntersectWith(running);

        var decision = _gameTrigger.Tick(AutoConnectOnGameStart, Mode, runningVpnApps);

        if (decision.Action != GameTriggerAction.None)
        {
            Logging.Verbose("GPN", "game_trigger",
                ("action", decision.Action),
                ("runningVpnApps", string.Join(",", runningVpnApps)),
                ("autoConnect", AutoConnectOnGameStart),
                ("mode", Mode));
        }

        switch (decision.Action)
        {
            case GameTriggerAction.Connect:
                _settingModeProgrammatically = true;
                Mode = ModeManual; // auto-apply happens through OnModeChanged
                StatusText = string.Format(ResUI.ManualGameTriggerConnected, string.Join(", ", decision.NewlyStarted));
                break;

            case GameTriggerAction.Restore:
                TriggerPending = false;
                TriggerPendingText = "";
                _settingModeProgrammatically = true;
                Mode = ModeOff;
                StatusText = ResUI.ManualGameTriggerRestored;
                break;
        }

        if (_gameTrigger.TriggerActive)
        {
            // Keep the banner current as games start and stop.
            TriggerPending = true;
            TriggerPendingText = string.Format(ResUI.ManualGameTriggerPending, string.Join(", ", runningVpnApps));
        }
    }

    /// <summary>Forgets the pending mode restore after the user takes over.</summary>
    private void CancelGameTrigger()
    {
        _gameTrigger.Reset(); // next snapshot re-seeds the baseline
        TriggerPending = false;
        TriggerPendingText = "";
    }

    /// <summary>"İptal" on the pending banner: stay in Manuel, do not auto-restore.</summary>
    private void CancelGameTriggerFromUi()
    {
        CancelGameTrigger();
        StatusText = ResUI.ManualGameTriggerCancelled;
    }

    /// <summary>
    /// Checks only the listed process names instead of enumerating the whole system,
    /// so the periodic scan stays cheap.
    /// </summary>
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

    private void UpdateLiveStatus(HashSet<string>? running)
    {
        var byProcess = new Dictionary<string, List<ConnectionMonitorItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Monitor.Connections)
        {
            if (c.ProcessName.IsNullOrEmpty())
            {
                continue;
            }
            if (!byProcess.TryGetValue(c.ProcessName, out var list))
            {
                byProcess[c.ProcessName] = list = new List<ConnectionMonitorItem>();
            }
            list.Add(c);
        }

        var tunOnForList = Apps.Any(a => a.Action is "vpn" or "vpn+proxy" or "warp");
        foreach (var app in Apps)
        {
            if (app.EntryType != "app")
            {
                app.IsRunning = false;
                app.RunStatusText = "—";
                app.LiveRouteTag = "";
                app.LiveRouteText = "—";
                app.LiveConnectionCount = 0;
                app.NeedsTun = false;
                app.ExeMissing = false;
                continue;
            }

            // Running status and missing-exe detection need the process scan; when the
            // scan is gated off (hidden window / non-manual mode) keep the last values.
            if (running is not null)
            {
                var processName = Path.GetFileNameWithoutExtension(app.Value);
                var isRunning = processName.IsNotEmpty() && running.Contains(processName);
                app.IsRunning = isRunning;
                app.RunStatusText = isRunning ? ResUI.ManualRunning : ResUI.ManualNotRunning;
                app.ExeMissing = IsExeMissing(app);
            }

            var live = byProcess.TryGetValue(app.Value, out var list) ? list : null;
            if (live is { Count: > 0 })
            {
                var tag = live
                    .GroupBy(x => x.RouteTag)
                    .OrderByDescending(g => g.Count())
                    .First()
                    .Key;
                app.LiveRouteTag = tag.IsNullOrEmpty() ? Global.DirectTag : tag;
                app.LiveRouteText = live.Count > 1
                    ? $"{RouteText(app.LiveRouteTag)} · {live.Count}"
                    : RouteText(app.LiveRouteTag);
                app.LiveConnectionCount = live.Count;
            }
            else
            {
                app.LiveRouteTag = "";
                app.LiveRouteText = "—";
                app.LiveConnectionCount = 0;
            }

            // When the process scan is gated off (hidden window / Global VPN / Off
            // mode) the live connection list is the source of truth: a row with active
            // connections is running — never show "Not running" next to live traffic.
            if (running is null && live is { Count: > 0 })
            {
                app.IsRunning = true;
                app.RunStatusText = ResUI.ManualRunning;
            }

            // GPN kullanımında eklenen uygulamanın GERÇEK sunucu uç noktalarını
            // kaydet: uygulama (efektif olarak tünellenen kümede) çalışıp genel bir
            // adrese bağlandığında uzak uç nokta gpn_app_endpoints'e gözlem olarak
            // düşer. Kayıt bellekte yapılır; periyodik flush diske yazar. Aynı uç
            // nokta seti program açılışında (doğrudan yol = önce) ve GPN bağlantısı
            // kurulunca (tünel yolu = sonra) ölçülerek öncesi/sonrası üretilir.
            if (IsTunneledApp(app) && live is { Count: > 0 })
            {
                RecordAppEndpoints(app.Value, live);
            }

            // Proxy-only apps whose live connections go to public addresses are bypassing
            // the system proxy; they need the TUN (VPN) option to be captured.
            app.NeedsTun = app.Action == "proxy"
                && !tunOnForList
                && live is { Count: > 0 }
                && live.Any(c => RemoteIsPublicDirect(c.RemoteAddress));
        }
        AnyNeedsTun = Apps.Any(a => a.NeedsTun);
    }

    /// <summary>True when an app entry points to an executable that no longer exists.</summary>
    private static bool IsExeMissing(SplitTunnelAppItem app)
    {
        return app.EntryType == "app" && app.ExePath.IsNotEmpty() && !File.Exists(app.ExePath);
    }

    /// <summary>True when the remote address is a public (routable) destination.</summary>
    private static bool RemoteIsPublicDirect(string? remoteAddress)
    {
        var host = ExtractHost(remoteAddress);
        if (host is null)
        {
            return false;
        }
        if (IPAddress.TryParse(host, out _))
        {
            return !Utils.IsPrivateNetwork(host);
        }
        return true; // hostname remote → a direct connection, not the local proxy
    }

    /// <summary>Extracts the host part of a "host:port", "[v6]:port" or "*" address.</summary>
    private static string? ExtractHost(string? address)
    {
        if (address.IsNullOrEmpty() || address == "*")
        {
            return null;
        }
        if (address![0] == '[')
        {
            var end = address.IndexOf(']');
            return end > 1 ? address[1..end] : null;
        }
        var idx = address.LastIndexOf(':');
        return idx > 0 ? address[..idx] : address;
    }

    /// <summary>
    /// Uygulama, GPN yakalama köprüsünün hedef kümesinde mi (efektif tünellenen)?
    /// GpnTargetResolverBridge.ExtractTargetNames ile AYNI kural: beyaz listede
    /// "vpn" eylemli, kara listede (dışlama) kuralların tünele çevirdiği "direct"
    /// eylemli girişler. Kayıt yalnızca bu kümeye yapılır — kullanıcının GPN için
    /// eklediği uygulamaların bağlantıları veritabanına düşer.
    /// </summary>
    private bool IsTunneledApp(SplitTunnelAppItem app)
    {
        if (app.EntryType != "app")
        {
            return false;
        }
        var tunneledAction = InvertManualRouting ? "direct" : "vpn";
        return string.Equals(app.Action, tunneledAction, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Uygulamanın canlı bağlantılarından genel (public) uzak uç noktaları gerçek
    /// ping mağazasına gözlem olarak kaydeder. Özel/yerel adresler ve "*" gibi
    /// sahte uç noktalar atlanır — ölçüm yalnızca gerçek internet sunucularına
    /// yapılmalıdır.
    /// </summary>
    private void RecordAppEndpoints(string processName, List<ConnectionMonitorItem> live)
    {
        foreach (var connection in live)
        {
            if (connection.IsPrivate
                || connection.RemoteAddress.IsNullOrEmpty()
                || connection.RemoteAddress is "*" or "*:0" or "0.0.0.0")
            {
                continue;
            }
            _endpointStore.Observe(processName, connection.RemoteAddress, connection.Protocol);
        }
    }

    private async Task LoadAsync()
    {
        // Restoring saved state must not trigger an auto-apply / core reload.
        _suppressAutoApply = true;
        try
        {
            _routingItem = await ConfigHandler.GetDefaultRouting(_config);
            ActiveRoutingRemarks = _routingItem?.Remarks ?? string.Empty;

            Transport = _config.ConnectionItem.Transport is "tun" or "proxy"
                ? _config.ConnectionItem.Transport
                : "";
            Apps.Clear();
            foreach (var r in _config.ConnectionItem.ManualRoutes ?? [])
            {
                if (r.Value.IsNullOrEmpty())
                {
                    continue;
                }
                var item = CreateItem(r);
                item.RouteTag = MapActionToOutbound(item.Action);
                item.RouteText = RouteText(item.RouteTag);
                item.ExeMissing = IsExeMissing(item);
                Apps.Add(item);
            }
        }
        finally
        {
            _suppressAutoApply = false;
            _initialized = true;
        }
    }

    private async Task AddAppAsync()
    {
        var fileName = await BrowseExeInteraction.Handle(Unit.Default);
        if (fileName.IsNullOrEmpty() || !File.Exists(fileName))
        {
            return;
        }

        fileName = Path.GetFullPath(fileName);
        var processName = Path.GetFileName(fileName);
        if (Apps.Any(a => a.EntryType == "app" && a.Value.Equals(processName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = ResUI.ManualAlreadyExists;
            NoticeManager.Instance.Enqueue(ResUI.ManualAlreadyExists);
            return;
        }

        var displayName = GetFileDescription(fileName) ?? processName;
        var suggested = SuggestActionForApp(processName, displayName);
        var setting = new ManualRouteSetting
        {
            EntryType = "app",
            Value = processName,
            DisplayName = displayName,
            ExePath = fileName,
            Action = suggested,
            SuggestedAction = suggested != "proxy" ? suggested : "",
        };
        var item = AddManualRoute(setting);
        NotifySuggestedAction(item);
    }

    private async Task AddDomainAsync()
    {
        var input = DomainInput?.Trim() ?? "";
        if (input.IsNullOrEmpty() || input.Contains(' '))
        {
            StatusText = ResUI.ManualInvalidDomain;
            NoticeManager.Instance.Enqueue(ResUI.ManualInvalidDomain);
            return;
        }

        // Accept a domain, an IP/CIDR and an optional ":port" suffix.
        if (!ManualRouteParser.TrySplitPort(input, out var host, out var port)
            || host.IsNullOrEmpty())
        {
            StatusText = ResUI.ManualInvalidDomain;
            NoticeManager.Instance.Enqueue(ResUI.ManualInvalidDomain);
            return;
        }
        var entryType = ManualRouteParser.ClassifyEntryType(host);
        if (!ManualRouteParser.IsValidHost(host, entryType))
        {
            StatusText = ResUI.ManualInvalidDomain;
            NoticeManager.Instance.Enqueue(ResUI.ManualInvalidDomain);
            return;
        }

        if (Apps.Any(a => a.EntryType == entryType && a.Value.Equals(host, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = ResUI.ManualAlreadyExists;
            NoticeManager.Instance.Enqueue(ResUI.ManualAlreadyExists);
            return;
        }

        var setting = new ManualRouteSetting
        {
            EntryType = entryType,
            Value = host,
            Port = port,
            DisplayName = host,
            Action = "proxy",
        };
        AddManualRoute(setting);
        DomainInput = "";
    }

    private SplitTunnelAppItem AddManualRoute(ManualRouteSetting setting)
    {
        var item = CreateItem(setting);
        item.RouteTag = MapActionToOutbound(item.Action);
        item.RouteText = RouteText(item.RouteTag);
        Apps.Add(item); // CollectionChanged → OnListChanged persists + auto-applies
        SelectedApp = item;
        return item;
    }

    /// <summary>
    /// Adds a live monitor connection's remote address to the manual list as an IP or
    /// domain row (one click from the monitoring table's context menu).
    /// </summary>
    public void AddRemoteToManualList(ConnectionMonitorItem? item)
    {
        if (item is null || item.RemoteAddress.IsNullOrEmpty())
        {
            return;
        }
        if (item.IsPrivate || item.RemoteAddress is "*" or "*:0")
        {
            StatusText = ResUI.ManualSkipLocalAddress;
            NoticeManager.Instance.Enqueue(ResUI.ManualSkipLocalAddress);
            return;
        }

        if (!ManualRouteParser.TrySplitPort(item.RemoteAddress.Trim(), out var host, out var port)
            || host.IsNullOrEmpty()
            || host is "*" or "0.0.0.0" or "::" or "[::]" or "0:0:0:0:0:0:0:0")
        {
            StatusText = ResUI.ManualSkipLocalAddress;
            NoticeManager.Instance.Enqueue(ResUI.ManualSkipLocalAddress);
            return;
        }
        var entryType = ManualRouteParser.ClassifyEntryType(host);
        if (!ManualRouteParser.IsValidHost(host, entryType))
        {
            StatusText = ResUI.ManualSkipLocalAddress;
            NoticeManager.Instance.Enqueue(ResUI.ManualSkipLocalAddress);
            return;
        }
        if (Apps.Any(a => a.EntryType == entryType && a.Value.Equals(host, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = ResUI.ManualAlreadyExists;
            NoticeManager.Instance.Enqueue(ResUI.ManualAlreadyExists);
            return;
        }

        var setting = new ManualRouteSetting
        {
            EntryType = entryType,
            Value = host,
            Port = port,
            DisplayName = host,
            Action = "proxy",
        };
        AddManualRoute(setting);
    }

    /// <summary>
    /// Adds a domain/IP rule from the dashboard (e.g. the one-click BSG API WARP
    /// entry). Reuses the native parser so every add flow behaves identically; the
    /// entry gets the requested connection option (default proxy when empty).
    /// Returns false when the value is invalid or a matching entry already exists
    /// (the existing entry keeps its action — no silent overwrite).
    /// </summary>
    public bool AddDomainRoute(string input, string action, string? displayName = null)
    {
        var normalized = input?.Trim().Trim('"') ?? string.Empty;
        if (normalized.IsNullOrEmpty() || normalized.Contains(' ') || !IsKnownAction(action))
        {
            Logging.Verbose("GPN", "add_domain_route_rejected",
                ("value", normalized), ("action", action), ("reason", "invalid_input"));
            return false;
        }
        if (!ManualRouteParser.TrySplitPort(normalized, out var host, out var port) || host.IsNullOrEmpty())
        {
            return false;
        }
        var entryType = ManualRouteParser.ClassifyEntryType(host);
        if (!ManualRouteParser.IsValidHost(host, entryType))
        {
            return false;
        }
        if (Apps.Any(a => a.EntryType == entryType
            && a.Value.Equals(host, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = ResUI.ManualAlreadyExists;
            NoticeManager.Instance.Enqueue(ResUI.ManualAlreadyExists);
            Logging.Verbose("GPN", "add_domain_route_rejected",
                ("entryType", entryType), ("value", host), ("reason", "duplicate"));
            return false;
        }

        AddManualRoute(new ManualRouteSetting
        {
            EntryType = entryType,
            Value = host,
            Port = port,
            DisplayName = displayName.IsNotEmpty() ? displayName! : host,
            Action = action,
        });
        Logging.Verbose("GPN", "add_domain_route",
            ("entryType", entryType), ("value", host), ("action", action));
        return true;
    }

    /// <summary>
    /// Applies a route selected by the modern dashboard to a listed entry. Existing
    /// entries are edited in place. Apps arrive as "&lt;name&gt;.exe" and a missing app
    /// row is added safely with only its process name; any other value addresses an
    /// existing domain/IP rule row (its route dropdown is rendered from the same
    /// table). The native routing generator remains the single source of truth, so
    /// the dashboard cannot create a second, conflicting rule format.
    /// </summary>
    public async Task<bool> SetDashboardAppRouteAsync(string processName, string? displayName, string action)
    {
        var raw = processName?.Trim() ?? string.Empty;
        if (raw.IsNullOrEmpty() || !IsKnownAction(action))
        {
            Logging.Verbose("GPN", "set_route_rejected",
                ("processName", raw), ("action", action), ("reason", "invalid_input"));
            return false;
        }

        // Apps carry a .exe name; a non-exe value addresses a domain/IP row.
        string entryType;
        string value;
        if (raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            value = Path.GetFileName(raw);
            entryType = "app";
        }
        else
        {
            var existing = Apps.FirstOrDefault(a => a.EntryType is "domain" or "ip"
                && a.Value.Equals(raw, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                Logging.Verbose("GPN", "set_route_rejected",
                    ("processName", raw), ("action", action), ("reason", "no_matching_entry"));
                return false;
            }
            value = raw;
            entryType = existing.EntryType;
        }

        var item = Apps.FirstOrDefault(a => a.EntryType == entryType
            && a.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            if (entryType != "app")
            {
                return false;
            }
            item = AddManualRoute(new ManualRouteSetting
            {
                EntryType = "app",
                Value = value,
                DisplayName = displayName.IsNotEmpty() ? displayName! : value,
                Action = action,
            });
            Logging.Verbose("GPN", "set_route_added",
                ("processName", value), ("action", action), ("mode", Mode));
        }
        else
        {
            var oldAction = item.Action;
            item.Action = action;
            item.UpdateSuggested();
            item.RouteTag = MapActionToOutbound(item.Action);
            item.RouteText = RouteText(item.RouteTag);
            OnListChanged();
            Logging.Verbose("GPN", "set_route_changed",
                ("processName", value), ("old", oldAction), ("new", action));
        }

        SelectedApp = item;
        if (Mode == ModeManual)
        {
            await ApplyAsync(silent: true);
        }
        else
        {
            StatusText = ResUI.ConnectionSaved;
        }
        return true;
    }

    /// <summary>
    /// Opens the same executable picker used by the native ConnectionView. This is
    /// intentionally exposed as a small command bridge instead of duplicating file
    /// parsing in the WebView layer.
    /// </summary>
    public async Task AddDashboardAppAsync(string fileName)
    {
        if (fileName.IsNullOrEmpty() || !File.Exists(fileName))
        {
            Logging.Verbose("GPN", "add_app_rejected", ("fileName", fileName ?? "(null)"), ("reason", "file_missing"));
            return;
        }
        Logging.Verbose("GPN", "add_app", ("fileName", fileName));
        await AddDroppedFilesAsync([fileName]);
    }

    /// <summary>Recommended default connection option for a known app (games → VPN).</summary>
    private static string SuggestActionForApp(string processName, string displayName)
    {
        return KnownAppCatalog.SuggestAction(processName, displayName);
    }

    /// <summary>Shows a hint when an app was detected as a game and suggested VPN (TUN).</summary>
    private void NotifySuggestedAction(SplitTunnelAppItem item)
    {
        if (item.Action != "proxy")
        {
            StatusText = string.Format(ResUI.ManualGameSuggested, item.DisplayName);
        }
    }

    /// <summary>Opens the edit dialog for the selected entry and applies changes on OK.</summary>
    private async Task EditSelectedAppAsync()
    {
        if (SelectedApp is null)
        {
            return;
        }

        var dialog = new ManualRouteEditViewModel(SelectedApp, ManualActions);
        var result = await AppManager.Instance.WindowDialog.ShowDialogAsync(dialog);
        if (result != true)
        {
            return;
        }

        var item = SelectedApp;
        var oldValue = item.Value;
        item.Value = dialog.Value ?? "";
        item.Port = dialog.Port ?? "";
        item.DisplayName = dialog.DisplayName ?? item.Value;
        item.Action = NormalizeAction(dialog.Action);

        if (item.EntryType == "app")
        {
            item.ProcessName = item.Value;
            // If the process name changed, the old exe path no longer matches the entry.
            var oldExeName = Path.GetFileNameWithoutExtension(oldValue);
            var newExeName = Path.GetFileNameWithoutExtension(item.Value);
            if (!oldExeName.Equals(newExeName, StringComparison.OrdinalIgnoreCase))
            {
                item.ExePath = "";
            }
        }

        item.RouteTag = MapActionToOutbound(item.Action);
        item.RouteText = RouteText(item.RouteTag);
        item.ExeMissing = IsExeMissing(item);

        OnListChanged(); // persists + auto-applies (Manuel mode)
        await RefreshLiveStatusAsync();
    }

    /// <summary>
    /// Imports multiple apps/domains from the clipboard in one go. Each line (or
    /// comma/semicolon separated token) is parsed as an app or a domain entry.
    /// </summary>
    private async Task ImportClipboardAsync()
    {
        var text = await GetClipboardTextInteraction.Handle(Unit.Default);
        if (text.IsNullOrEmpty())
        {
            StatusText = ResUI.ManualImportEmpty;
            NoticeManager.Instance.Enqueue(ResUI.ManualImportEmpty);
            return;
        }
        await AddTokensAsync(SplitImportText(text));
    }

    /// <summary>Adds dropped .exe files (from Explorer) to the manual list.</summary>
    public async Task AddDroppedFilesAsync(IEnumerable<string> fileNames)
    {
        var added = 0;
        var skipped = 0;
        var batch = new List<SplitTunnelAppItem>();

        foreach (var raw in fileNames)
        {
            var path = raw.Trim().Trim('"');
            if (path.IsNullOrEmpty() || !File.Exists(path))
            {
                skipped++;
                continue;
            }
            var fileName = Path.GetFileName(path);
            if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }
            if (Apps.Any(a => a.EntryType == "app" && a.Value.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }

            var displayName = GetFileDescription(path) ?? fileName;
            var suggested = SuggestActionForApp(fileName, displayName);
            var setting = new ManualRouteSetting
            {
                EntryType = "app",
                Value = fileName,
                DisplayName = displayName,
                ExePath = Path.GetFullPath(path),
                Action = suggested,
                SuggestedAction = suggested != "proxy" ? suggested : "",
            };
            var item = CreateItem(setting);
            item.RouteTag = MapActionToOutbound(item.Action);
            item.RouteText = RouteText(item.RouteTag);
            item.ExeMissing = IsExeMissing(item);
            batch.Add(item);
            added++;
        }

        ApplyBatch(batch);
        ReportImport(added, skipped);
    }

    /// <summary>Adds dropped text (a list copied from another app) like the clipboard import.</summary>
    public async Task AddDroppedTextAsync(string text)
    {
        if (text.IsNullOrEmpty())
        {
            return;
        }
        await AddTokensAsync(SplitImportText(text));
    }

    private static string[] SplitImportText(string text) => text.Split(
        ['\n', '\r', ',', ';', '|', '\t'],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Classifies each token (app path / process name / domain) and adds the valid,
    /// non-duplicate entries to the list in a single batch.
    /// </summary>
    private async Task AddTokensAsync(IEnumerable<string> tokens)
    {
        var added = 0;
        var skipped = 0;
        var batch = new List<SplitTunnelAppItem>();

        foreach (var raw in tokens)
        {
            var token = NormalizeImportToken(raw);
            if (token is null)
            {
                skipped++;
                continue;
            }

            var setting = BuildImportSetting(token, out var valid);
            if (!valid || setting is null)
            {
                skipped++;
                continue;
            }
            if (Apps.Any(a => a.EntryType == setting.EntryType && a.Value.Equals(setting.Value, StringComparison.OrdinalIgnoreCase)))
            {
                skipped++;
                continue;
            }

            var item = CreateItem(setting);
            item.RouteTag = MapActionToOutbound(item.Action);
            item.RouteText = RouteText(item.RouteTag);
            item.ExeMissing = IsExeMissing(item);
            batch.Add(item);
            added++;
        }

        ApplyBatch(batch);
        ReportImport(added, skipped);
    }

    private static string? NormalizeImportToken(string raw)
    {
        var token = raw.Trim().Trim('"');
        if (token.IsNullOrEmpty() || token.Contains(' '))
        {
            return null;
        }
        // Strip a scheme/prefix so "https://discord.gg" imports as the domain.
        if (token.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || token.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var schemeEnd = token.IndexOf("://", StringComparison.Ordinal) + 3;
            token = token[schemeEnd..];
        }
        token = token.TrimEnd('/');
        return token.IsNullOrEmpty() ? null : token;
    }

    /// <summary>Adds a batch of parsed entries with one persist + one auto-apply.</summary>
    private void ApplyBatch(List<SplitTunnelAppItem> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }
        _batchUpdating = true;
        try
        {
            foreach (var item in batch)
            {
                Apps.Add(item);
            }
        }
        finally
        {
            _batchUpdating = false;
        }
        SelectedApp = batch[^1];
        OnListChanged(); // single persist + auto-apply for the whole batch
    }

    private void ReportImport(int added, int skipped)
    {
        var message = string.Format(ResUI.ManualImportResult, added, skipped);
        StatusText = message;
        NoticeManager.Instance.Enqueue(message);
    }

    /// <summary>Classifies a clipboard/drop token as an app, domain or IP setting.</summary>
    private ManualRouteSetting? BuildImportSetting(string token, out bool valid)
    {
        valid = false;
        if (token.IsNullOrEmpty())
        {
            return null;
        }

        var isExe = token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        var isWindowsPath = token.Contains('\\');
        if (isExe || isWindowsPath)
        {
            var fileName = Path.GetFileName(token);
            if (fileName.IsNullOrEmpty() || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            var exePath = File.Exists(token) ? Path.GetFullPath(token) : "";
            var displayName = exePath.IsNotEmpty() ? (GetFileDescription(exePath) ?? fileName) : fileName;
            var suggested = SuggestActionForApp(fileName, displayName);
            var setting = new ManualRouteSetting
            {
                EntryType = "app",
                Value = fileName,
                DisplayName = displayName,
                ExePath = exePath,
                Action = suggested,
                SuggestedAction = suggested != "proxy" ? suggested : "",
            };
            valid = true;
            return setting;
        }

        // Domain, IP/CIDR — optionally with a ":port" suffix.
        if (!ManualRouteParser.TrySplitPort(token, out var host, out var port) || host.IsNullOrEmpty())
        {
            return null;
        }
        var entryType = ManualRouteParser.ClassifyEntryType(host);
        if (!ManualRouteParser.IsValidHost(host, entryType))
        {
            return null;
        }

        var address = new ManualRouteSetting
        {
            EntryType = entryType,
            Value = host,
            Port = port,
            DisplayName = host,
            Action = "proxy",
        };
        valid = true;
        return address;
    }

    private void RemoveSelectedApp()
    {
        if (SelectedApp is null)
        {
            Logging.Verbose("GPN", "remove_app_skipped", "no selection");
            return;
        }
        var removed = SelectedApp.DisplayName;
        Logging.Verbose("GPN", "remove_app", ("name", removed), ("action", SelectedApp.Action));
        Apps.Remove(SelectedApp); // CollectionChanged → OnListChanged persists + auto-applies
        SelectedApp = null;
        StatusText = $"Removed: {removed}";
    }

    /// <summary>
    /// Moves a manual-list entry one step up/down in the routing order. Rule order
    /// equals list order and cores (mihomo/sing-box) match rules top-down, so a
    /// domain rule must sit ABOVE the process rule it should take precedence over
    /// (e.g. a BSG API domain above the game's VPN process rule). The move persists
    /// and auto-applies through the normal collection-change path (Manuel mode).
    /// Returns false when the entry is unknown or already at the edge.
    /// </summary>
    public bool MoveManualRoute(string entryType, string value, bool up)
    {
        if (value.IsNullOrEmpty() || entryType is not ("app" or "domain" or "ip"))
        {
            Logging.Verbose("GPN", "move_route_rejected", ("entryType", entryType), ("value", value ?? "(null)"), ("reason", "invalid_input"));
            return false;
        }

        var index = -1;
        for (var i = 0; i < Apps.Count; i++)
        {
            if (Apps[i].EntryType == entryType
                && Apps[i].Value.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }
        if (index < 0)
        {
            Logging.Verbose("GPN", "move_route_rejected", ("entryType", entryType), ("value", value), ("reason", "unknown_entry"));
            return false;
        }

        var target = up ? index - 1 : index + 1;
        if (target < 0 || target >= Apps.Count)
        {
            return false;
        }

        Logging.Verbose("GPN", "move_route",
            ("entryType", entryType), ("value", value), ("direction", up ? "up" : "down"),
            ("from", index), ("to", target));
        var item = Apps[index];
        Apps.RemoveAt(index);
        Apps.Insert(target, item); // CollectionChanged → persist + debounced auto-apply
        return true;
    }

    private async Task ApplyAsync(bool silent = false)
    {
        Logging.Verbose("GPN", "apply_start", ("mode", Mode), ("transport", Transport), ("silent", silent));

        // A manual apply supersedes any pending auto-apply.
        _autoApplyCts?.Cancel();
        _autoApplyCts = null;

        var requiresTun = ComputeRequiresTun();

        // OpenVPN creates and owns the OS tunnel itself; it cannot be reduced to
        // this app's local SOCKS/system-proxy capture. Force the semantically
        // correct transport even if the dashboard's last generic choice was proxy.
        var activeNode = await ConfigHandler.GetDefaultServer(_config);
        if (Mode != ModeOff && activeNode?.CoreType == ECoreType.openvpn)
        {
            Transport = "tun";
            requiresTun = true;
        }

        // Persist the manual list and the routing direction before writing rules.
        if (Mode == ModeManual)
        {
            SyncManualRoutesToConfig();
            _config.ConnectionItem.InvertManualRouting = InvertManualRouting;
        }

        // Routing rules are written FIRST: they only persist config and never need
        // elevation, and leaving the previous mode's catch-all (e.g. a GPN whitelist
        // "0-65535 direct" rule) in place while the UI reports Global VPN is what
        // caused silent leaks. The TUN elevation check below must not skip this.
        var (success, message) = await SaveRulesAsync();
        if (!success)
        {
            StatusText = message;
            return;
        }

        if (requiresTun && Utils.IsWindows() && !Utils.IsAdministrator())
        {
            NeedAdmin = true;
            StatusText = ResUI.ConnectionNeedAdmin;
            return;
        }
        NeedAdmin = false;

        _config.TunModeItem.EnableTun = requiresTun;
        _config.ConnectionItem ??= new();
        _config.ConnectionItem.Mode = Mode;
        _config.ConnectionItem.Transport = Mode == ModeOff
            ? ""
            : Transport is "tun" or "proxy" ? Transport : "";
        // The independent AoGPN-style system-proxy preference is intentionally not
        // overwritten by connection mode changes. The policy applies a temporary
        // ForcedChange only when this active connection actually needs proxy capture.
        await ConfigSaveQueue.SaveAndWaitAsync(_config);

        StatusBarViewModel.Instance.EnableTun = requiresTun;
        StatusBarViewModel.Instance.SystemProxySelected = (int)_config.SystemProxyItem.SysProxyType;

        // Kesintisiz (make-before-break) rota uygulaması: çalışan GPN mihomo superset
        // config'ine sahipse bu değişiklik (mod / yön / giriş rotası) çekirdek reload'u
        // yerine canlı grup seçimleriyle uygulanır — bağlantı kesilmez, mevcut oturumlar
        // doğal olarak sürer. Kapılar tutmuyorsa (superset oturum yok, giriş listesi
        // yapısal olarak değişti, API erişilemez) eski ReloadRequested yolu korunur.
        if (!await TryApplyGpnRoutingSoftAsync())
        {
            StatusBarViewModel.Instance.ReloadRequested.Publish();
        }

        // The routing rules changed; drop the monitor's cached routing and refresh so
        // the live route tags match the newly applied rules.
        Monitor.InvalidateRoutingCache();
        _ = Monitor.RefreshAsync();

        StatusText = ResUI.ConnectionSaved;
        if (!silent)
        {
            NoticeManager.Instance.Enqueue(ResUI.ConnectionSaved);
        }
        WarnWarpFallbackIfNeeded();
    }

    /// <summary>
    /// Güncel mod/yön/giriş listesini çalışan GPN mihomo superset oturumuna grup seçimi
    /// olarak uygulamayı dener. <c>true</c> dönerse çağıran (ApplyAsync) reload
    /// yayınlamaz — çekirdek/TUN/bağlantı yerinde kalır. Superset oturum yoksa, giriş
    /// listesi çalışan config'le birebir aynı değilse (ekleme/silme/taşıma) veya API
    /// doğrulaması başarısız olursa <c>false</c> → legacy reload (config yeniden üretilir).
    /// </summary>
    private async Task<bool> TryApplyGpnRoutingSoftAsync()
    {
        var policy = GpnSoftRouting.BuildPolicy(Mode, InvertManualRouting, Apps, _config);
        var applied = await GpnSoftPolicyApplier.TryApplyAsync(policy);
        Logging.Verbose("GPN", applied ? "soft_routing_applied" : "soft_routing_fallback_reload",
            ("mode", Mode), ("invert", InvertManualRouting), ("entries", Apps.Count));
        return applied;
    }

    /// <summary>
    /// Rewrites the managed routing rules for the active mode. Only entries on the manual
    /// list get rules in Manuel mode; everything else falls through to the direct catch-all
    /// rule, so unlisted apps are never sent through the VPN/proxy.
    /// </summary>
    private async Task<(bool Success, string Message)> SaveRulesAsync()
    {
        // Always re-read the active routing item so managed rules land on the item
        // the core actually uses at reload time, even when another view or preset
        // changed the active routing after this ViewModel was first loaded.
        _routingItem = await ConfigHandler.GetDefaultRouting(_config);

        if (_routingItem is null)
        {
            return (false, ResUI.SplitTunnelNoRouting);
        }

        var rules = _routingItem.RuleSet.IsNullOrEmpty()
            ? new List<RulesItem>()
            : JsonUtils.Deserialize<List<RulesItem>>(_routingItem.RuleSet) ?? new List<RulesItem>();

        var preserved = rules.Where(r => !ManualRoutingRules.IsManagedRule(r)).ToList();
        // Pure rule generation: per-entry rules (Manuel mode, honouring the whitelist/
        // blacklist direction) + catch-all for the mode.
        var managed = ManualRoutingRules.BuildManagedRules(Mode, Apps, InvertManualRouting);

        var final = managed.Concat(preserved).ToList();
        _routingItem.RuleSet = JsonUtils.Serialize(final, false);
        _routingItem.RuleNum = final.Count;

        if (await ConfigHandler.SaveRoutingItem(_config, _routingItem) == 0)
        {
            return (true, ResUI.ConnectionSaved);
        }

        return (false, ResUI.OperationFailed);
    }

    private bool ComputeRequiresTun()
    {
        // Off always releases network capture; it does not clear an independent
        // system-proxy preference selected from the AoGPN-style proxy control.
        if (Mode == ModeOff)
        {
            return false;
        }

        // Per-process routing rules (and the Global VPN catch-all) only work when
        // sing-box can attribute connections to processes, which requires the TUN
        // inbound. A Proxy preference can never substitute for that — proxy capture
        // only reaches apps that honour the OS proxy, so VPN-routed apps and Global
        // VPN force TUN here regardless of the dashboard transport choice.
        if (Mode == ModeVpn)
        {
            return true;
        }
        if (Mode == ModeManual && Apps.Any(a => a.Action is "vpn" or "vpn+proxy" or "warp"))
        {
            return true;
        }

        // Remaining: Manuel mode with only proxy/direct/block entries (or an explicit
        // TUN choice). Here an explicit dashboard transport wins — Proxy capture does
        // serve proxy-honouring apps. System proxy ownership is resolved separately by
        // SystemProxyPolicy.
        if (Transport == "tun")
        {
            return true;
        }
        if (Transport == "proxy")
        {
            return false;
        }

        return false;
    }

    private static SplitTunnelAppItem CreateItem(ManualRouteSetting setting)
    {
        var isApp = setting.EntryType == "app";
        var item = new SplitTunnelAppItem
        {
            EntryType = isApp ? "app" : setting.EntryType,
            Value = setting.Value,
            Port = setting.Port ?? "",
            ProcessName = isApp ? setting.Value : "",
            DisplayName = setting.DisplayName.IsNotEmpty() ? setting.DisplayName : setting.Value,
            ExePath = isApp ? (setting.ExePath ?? "") : "",
            Action = NormalizeAction(setting.Action),
            SuggestedAction = setting.SuggestedAction ?? "",
        };
        item.UpdateSuggested();
        return item;
    }

    private static bool IsKnownAction(string? action)
    {
        // Keeps accepting the legacy "proxy" / "vpn+proxy" values so old configs
        // load untouched; NormalizeAction collapses them to "vpn" for new writes.
        return action is "vpn" or "proxy" or "vpn+proxy" or "direct" or "block" or "warp";
    }

    /// <summary>Collapses legacy connection options onto the three real actions.</summary>
    private static string NormalizeAction(string? action)
    {
        return action switch
        {
            "direct" => "direct",
            "block" => "block",
            "warp" => "warp",
            _ => "vpn", // vpn, and legacy proxy / vpn+proxy
        };
    }

    private static string MapActionToOutbound(string action) => ManualRoutingRules.MapActionToOutbound(action);

    private static string RouteText(string tag)
    {
        return tag switch
        {
            Global.ProxyTag => ResUI.ManualActionProxy,
            Global.DirectTag => ResUI.ManualActionDirect,
            Global.BlockTag => ResUI.ManualActionBlock,
            Global.WarpTag => ResUI.ManualActionWarp,
            _ => ResUI.ManualActionProxy,
        };
    }

    /// <summary>
    /// WARP rotası seçiliyken aktif bağlantı V2ray TCP ise kullanıcıyı uyar:
    /// WARP yalnızca WireGuard modunda çalışır, aksi hâlde kurallar VPN'e düşer.
    /// </summary>
    private void WarnWarpFallbackIfNeeded()
    {
        if (Apps.Any(a => a.Action == "warp")
            && _activeGpnMode is ConnectionMode.V2rayTCP)
        {
            StatusText = ResUI.ManualActionWarpNeedsWg;
            NoticeManager.Instance.Enqueue(ResUI.ManualActionWarpNeedsWg);
        }
    }

    private static string? GetFileDescription(string path)
    {
        if (path.IsNullOrEmpty() || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.FileDescription.IsNotEmpty()
                ? info.FileDescription
                : info.ProductName.IsNotEmpty() ? info.ProductName : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Copies the live per-app traffic (from sing-box, when running) onto the app list.
    /// The traffic items are indexed once by process name and executable path, so each
    /// app resolves with O(1) lookups instead of scanning the whole list (O(A×T) → O(A+T)).
    /// Name matching keeps priority over path matching, exactly like the previous per-item
    /// scan, so a traffic item is never counted twice for the same app.
    /// </summary>
    private void ApplyTrafficToApps()
    {
        var byName = new Dictionary<string, (long Download, long Upload, string Ips)>(StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, (long Download, long Upload, string Ips)>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in Monitor.AppTrafficItems)
        {
            if (t.AppName.IsNotEmpty())
            {
                AddTraffic(byName, t.AppName + ".exe", t.Download, t.Upload, t.ActiveIps);
            }
            if (t.ExePath.IsNotEmpty())
            {
                AddTraffic(byPath, t.ExePath, t.Download, t.Upload, t.ActiveIps);
            }
        }

        foreach (var app in Apps)
        {
            long download = 0;
            long upload = 0;
            var ips = "";

            if (app.ProcessName.IsNotEmpty() && byName.TryGetValue(app.ProcessName, out var nameHit))
            {
                download = nameHit.Download;
                upload = nameHit.Upload;
                ips = nameHit.Ips;
            }
            else if (app.ExePath.IsNotEmpty() && byPath.TryGetValue(app.ExePath, out var pathHit))
            {
                download = pathHit.Download;
                upload = pathHit.Upload;
                ips = pathHit.Ips;
            }

            app.Download = download;
            app.Upload = upload;
            app.ActiveIps = ips;
            app.DownloadText = download > 0 ? Utils.HumanFy(download) : "";
            app.UploadText = upload > 0 ? Utils.HumanFy(upload) : "";
        }
    }

    private static void AddTraffic(
        Dictionary<string, (long Download, long Upload, string Ips)> map,
        string key,
        long download,
        long upload,
        string ips)
    {
        if (map.TryGetValue(key, out var existing))
        {
            map[key] = (
                existing.Download > long.MaxValue - download ? long.MaxValue : existing.Download + download,
                existing.Upload > long.MaxValue - upload ? long.MaxValue : existing.Upload + upload,
                MergeIps(existing.Ips, ips));
        }
        else
        {
            map[key] = (download, upload, ips);
        }
    }

    /// <summary>Joins two comma-separated IP lists, collapsing duplicates.</summary>
    private static string MergeIps(string existing, string add)
    {
        if (add.IsNullOrEmpty())
        {
            return existing;
        }
        if (existing.IsNullOrEmpty())
        {
            return add;
        }
        var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in existing.Split(',').Concat(add.Split(',')))
        {
            var ip = part.Trim();
            if (ip.IsNotEmpty())
            {
                merged.Add(ip);
            }
        }
        return string.Join(", ", merged);
    }
}
