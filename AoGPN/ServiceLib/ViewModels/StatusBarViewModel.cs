namespace ServiceLib.ViewModels;

public class StatusBarViewModel : MyReactiveObject
{
    public Interaction<string, Unit> SetClipboardDataInteraction { get; } = new();
    public Interaction<Unit, string?> PasswordInputInteraction { get; } = new();
    public Interaction<Unit, Unit> DispatcherRefreshIconInteraction { get; } = new();
    public EventChannel<bool> SubscriptionsUpdateRequested { get; } = new();
    public EventChannel<bool?> ShowHideWindowRequested { get; } = new();

    // Tray quick-connect and connection-mode switching. The window subscribes and
    // routes these through the same gated paths the dashboard uses
    // (ToggleConnectionAsync / SetDashboardModeAsync), so a tray click never
    // bypasses the connection gate or the persisted routing rules.
    public EventChannel<Unit> ToggleConnectionRequested { get; } = new();
    public EventChannel<string> SetConnectionModeRequested { get; } = new();

    // WPF-side system-proxy requests (status-bar combobox, sidebar combobox, global
    // hotkeys) are routed to MainWindow so the proxy-only core is reconciled exactly
    // like the dashboard "set_system_proxy_mode" path. Merely applying the OS proxy
    // here while the connection is off would point Windows at a local SOCKS listener
    // that no core is running (proxy-only behaviour), leaving the browser with a
    // dead proxy instead of a working connection.
    public EventChannel<ESysProxyType> SystemProxyModeRequested { get; } = new();

    private static readonly Lazy<StatusBarViewModel> _instance = new(() => new());
    public static StatusBarViewModel Instance => _instance.Value;

    public EventChannel<string> SetDefaultServerRequested { get; } = new();
    public EventChannel<Unit> ReloadRequested { get; } = new();
    public EventChannel<Unit> AddServerViaScanRequested { get; } = new();
    public EventChannel<Unit> AddServerViaClipboardRequested { get; } = new();

    #region ObservableCollection

    public IObservableCollection<RoutingItem> RoutingItems { get; } = new ObservableCollectionExtended<RoutingItem>();

    public IObservableCollection<ComboItem> Servers { get; } = new ObservableCollectionExtended<ComboItem>();

    [Reactive]
    public RoutingItem SelectedRouting { get; set; }

    [Reactive]
    public ComboItem SelectedServer { get; set; }

    [Reactive]
    public bool BlServers { get; set; }

    #endregion ObservableCollection

    public ReactiveCommand<Unit, Unit> AddServerViaClipboardCmd { get; }
    public ReactiveCommand<Unit, Unit> AddServerViaScanCmd { get; }
    public ReactiveCommand<Unit, Unit> SubUpdateCmd { get; }
    public ReactiveCommand<Unit, Unit> SubUpdateViaProxyCmd { get; }
    public ReactiveCommand<Unit, Unit> CopyProxyCmdToClipboardCmd { get; }
    public ReactiveCommand<Unit, Unit> NotifyLeftClickCmd { get; }
    public ReactiveCommand<Unit, Unit> ShowWindowCmd { get; }
    public ReactiveCommand<Unit, Unit> HideWindowCmd { get; }
    public ReactiveCommand<Unit, Unit> QuickConnectCmd { get; }
    public ReactiveCommand<Unit, Unit> TestServerCmd { get; }
    public ReactiveCommand<Unit, Unit> ConnectionModeOffCmd { get; }
    public ReactiveCommand<Unit, Unit> ConnectionModeVpnCmd { get; }
    public ReactiveCommand<Unit, Unit> ConnectionModeManualCmd { get; }

    #region System Proxy

    [Reactive]
    public bool BlSystemProxyClear { get; set; }

    [Reactive]
    public bool BlSystemProxySet { get; set; }

    [Reactive]
    public bool BlSystemProxyNothing { get; set; }

    [Reactive]
    public bool BlSystemProxyPac { get; set; }

    public ReactiveCommand<Unit, Unit> SystemProxyClearCmd { get; }
    public ReactiveCommand<Unit, Unit> SystemProxySetCmd { get; }
    public ReactiveCommand<Unit, Unit> SystemProxyNothingCmd { get; }
    public ReactiveCommand<Unit, Unit> SystemProxyPacCmd { get; }

    [Reactive]
    public bool BlRouting { get; set; }

    [Reactive]
    public int SystemProxySelected { get; set; }

    [Reactive]
    public bool BlSystemProxyPacVisible { get; set; }

    #endregion System Proxy

    #region UI

    [Reactive]
    public string InboundDisplay { get; set; }

    [Reactive]
    public string InboundLanDisplay { get; set; }

    [Reactive]
    public string RunningServerDisplay { get; set; }

    [Reactive]
    public string RunningServerToolTipText { get; set; }

    [Reactive]
    public string RunningInfoDisplay { get; set; }

    [Reactive]
    public string SpeedProxyDisplay { get; set; }

    [Reactive]
    public string SpeedDirectDisplay { get; set; }

    /// <summary>Live status line shown at the top of the tray menu.</summary>
    [Reactive]
    public string TrayStatusLine { get; set; } = ResUI.TrayStatusIdle;

    /// <summary>
    /// 0 = idle, 1 = proxy-only core running, 2 = connection active. The WPF layer
    /// maps this to the tray status icon color.
    /// </summary>
    [Reactive]
    public int TrayStatusState { get; set; }

    [Reactive]
    public bool EnableTun { get; set; }

    [Reactive]
    public bool BlIsNonWindows { get; set; }

    #endregion UI

    public StatusBarViewModel()
    {
        _config = AppManager.Instance.Config;
        SelectedRouting = new();
        SelectedServer = new();
        RunningServerToolTipText = GetRunningServerToolTipText("-");
        BlSystemProxyPacVisible = Utils.IsWindows();
        BlIsNonWindows = Utils.IsNonWindows();

        if (_config.TunModeItem.EnableTun && AllowEnableTun())
        {
            EnableTun = true;
        }
        else
        {
            _config.TunModeItem.EnableTun = EnableTun = false;
        }

        #region WhenAnyValue && ReactiveCommand

        this.WhenAnyValue(
                x => x.SelectedRouting,
                y => y != null && !y.Remarks.IsNullOrEmpty())
            .Subscribe(async c => await RoutingSelectedChangedAsync(c));

        this.WhenAnyValue(
                x => x.SelectedServer,
                y => y != null && !y.Text.IsNullOrEmpty())
            .Subscribe(ServerSelectedChanged);

        SystemProxySelected = (int)_config.SystemProxyItem.SysProxyType;
        this.WhenAnyValue(
                x => x.SystemProxySelected,
                y => y >= 0)
            .Subscribe(async c => await DoSystemProxySelected(c));

        this.WhenAnyValue(
                x => x.EnableTun,
                y => y == true)
            .Subscribe(async c => await DoEnableTun(c));

        CopyProxyCmdToClipboardCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await CopyProxyCmdToClipboard();
        });

        NotifyLeftClickCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            // Toggle the window on every left tick. The click is delivered through
            // TaskbarIcon.TrayLeftMouseDown (see StatusBarView), which is raised once
            // per shell callback. No double-click suppression is applied here: the
            // routed event does not schedule trailing timer actions, and the toggle
            // is idempotent — a fast single vs double click both land the same
            // window state, which is what users expect from a tray icon.
            ShowHideWindowRequested.Publish(null);
            await Task.CompletedTask;
        });
        ShowWindowCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            ShowHideWindowRequested.Publish(true);
            await Task.CompletedTask;
        });
        HideWindowCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            ShowHideWindowRequested.Publish(false);
            await Task.CompletedTask;
        });
        QuickConnectCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            ToggleConnectionRequested.Publish();
            await Task.CompletedTask;
        });
        TestServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await TestServerAvailability();
        });
        ConnectionModeOffCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            SetConnectionModeRequested.Publish("off");
            await Task.CompletedTask;
        });
        ConnectionModeVpnCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            SetConnectionModeRequested.Publish("vpn");
            await Task.CompletedTask;
        });
        ConnectionModeManualCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            SetConnectionModeRequested.Publish("manual");
            await Task.CompletedTask;
        });

        AddServerViaClipboardCmd = ReactiveCommand.CreateFromTask(async () =>
            {
                await AddServerViaClipboard();
            });
        AddServerViaScanCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerViaScan();
        });
        SubUpdateCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await UpdateSubscriptionProcess(false);
        });
        SubUpdateViaProxyCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await UpdateSubscriptionProcess(true);
        });

        //System proxy
        SystemProxyClearCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SetListenerType(ESysProxyType.ForcedClear);
        });
        SystemProxySetCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SetListenerType(ESysProxyType.ForcedChange);
        });
        SystemProxyNothingCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SetListenerType(ESysProxyType.Unchanged);
        });
        SystemProxyPacCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SetListenerType(ESysProxyType.Pac);
        });

        #endregion WhenAnyValue && ReactiveCommand

        #region AppEvents

        AppEvents.DispatcherStatisticsRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async result => await UpdateStatistics(result));

        AppEvents.SysProxyChangeRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async result => await SetListenerType(result));

        #endregion AppEvents

        _ = Init();
    }

    private async Task Init()
    {
        await ConfigHandler.InitBuiltinRouting(_config);
        await RefreshRoutingsMenu();
        await InboundDisplayStatus();
        await ChangeSystemProxyAsync(_config.SystemProxyItem.SysProxyType, true);

        BlRouting = true;
    }

    private async Task CopyProxyCmdToClipboard()
    {
        var cmd = Utils.IsWindows() ? "set" : "export";
        var address = $"{Global.Loopback}:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}";

        var sb = new StringBuilder();
        sb.AppendLine($"{cmd} http_proxy={Global.HttpProtocol}{address}");
        sb.AppendLine($"{cmd} https_proxy={Global.HttpProtocol}{address}");
        sb.AppendLine($"{cmd} all_proxy={Global.Socks5Protocol}{address}");
        sb.AppendLine("");
        sb.AppendLine($"{cmd} HTTP_PROXY={Global.HttpProtocol}{address}");
        sb.AppendLine($"{cmd} HTTPS_PROXY={Global.HttpProtocol}{address}");
        sb.AppendLine($"{cmd} ALL_PROXY={Global.Socks5Protocol}{address}");

        await SetClipboardDataInteraction.Handle(sb.ToString());
    }

    private async Task AddServerViaClipboard()
    {
        AddServerViaClipboardRequested.Publish();
        await Task.Delay(1000);
    }

    private async Task AddServerViaScan()
    {
        AddServerViaScanRequested.Publish();
        await Task.Delay(1000);
    }

    private async Task UpdateSubscriptionProcess(bool blProxy)
    {
        SubscriptionsUpdateRequested.Publish(blProxy);
        await Task.Delay(1000);
    }

    public async Task RefreshServersBiz()
    {
        await RefreshServersMenu();

        //display running server
        var running = await ConfigHandler.GetDefaultServer(_config);
        if (running != null)
        {
            RunningServerDisplay = running.GetSummary();
            RunningServerToolTipText = GetRunningServerToolTipText(RunningServerDisplay);
        }
        else
        {
            RunningServerDisplay = ResUI.CheckServerSettings;
            RunningServerToolTipText = GetRunningServerToolTipText(RunningServerDisplay);
        }
    }

    private string GetRunningServerToolTipText(string serverInfo)
    {
        return Utils.IsLinux() ? Global.AppName : serverInfo;
    }

    private async Task RefreshServersMenu()
    {
        var lstModel = await AppManager.Instance.ProfileModels(_config.SubIndexId, "");

        if (lstModel?.Count > _config.GuiItem.TrayMenuServersLimit)
        {
            BlServers = false;
            return;
        }

        var models = new List<ComboItem>();
        BlServers = true;
        foreach (var it in lstModel)
        {
            var name = it.GetSummary();

            var item = new ComboItem() { ID = it.IndexId, Text = name };
            models.Add(item);
            if (_config.IndexId == it.IndexId)
            {
                SelectedServer = item;
            }
        }
        Servers.Clear();
        Servers.AddRange(models);
    }

    private void ServerSelectedChanged(bool c)
    {
        if (!c)
        {
            return;
        }
        if (SelectedServer == null)
        {
            return;
        }
        if (SelectedServer.ID.IsNullOrEmpty())
        {
            return;
        }
        SetDefaultServerRequested.Publish(SelectedServer.ID);
    }

    public async Task TestServerAvailability()
    {
        var item = await ConfigHandler.GetDefaultServer(_config);
        if (item == null)
        {
            return;
        }

        await TestServerAvailabilitySub(ResUI.Speedtesting);

        var check = await Task.Run(ConnectionHandler.RunAvailabilityCheckData);
        var msg = string.Format(ResUI.TestMeOutput, check.TimeMs, check.IpInfo?.ToString() ?? Global.None);

        NoticeManager.Instance.SendMessageEx(msg);
        await TestServerAvailabilitySub(msg);

        // Dashboard ana paneline yansıt: ölçülen gecikme + IP + sunucu adı. Ölçüm
        // yalnızca WPF durum çubuğunda (RunningInfoDisplay) ve bildirimde kalmak
        // yerine WebView2 Ping kartında da görünür — bağlantı kurulduktan sonra
        // gecikme/IP bilgisi ana panelde anında yer alır.
        AppEvents.AvailabilityCheckCompleted.Publish(new AvailabilityCheckResult(
            check.TimeMs,
            check.IpInfo?.Ip,
            check.IpInfo?.Country,
            item.GetSummary()));
    }

    private async Task TestServerAvailabilitySub(string msg)
    {
        RxSchedulers.MainThreadScheduler.Schedule(msg, (scheduler, msg) =>
        {
            _ = TestServerAvailabilityResult(msg);
            return Disposable.Empty;
        });
        await Task.CompletedTask;
    }

    public async Task TestServerAvailabilityResult(string msg)
    {
        RunningInfoDisplay = msg;
        await Task.CompletedTask;
    }

    #region System proxy and Routings

    private async Task SetListenerType(ESysProxyType type)
    {
        if (_config.SystemProxyItem.SysProxyType == type)
        {
            return;
        }
        _config.SystemProxyItem.SysProxyType = type;

        // Delegate the actual mode application to MainWindow: it persists the
        // independent preference, starts/stops the proxy-only core (so the OS proxy
        // always points at a live local listener), pushes the state to the dashboard
        // and updates the tray — exactly the dashboard path. Keeping the OS-proxy
        // call here would set Set/PAC while the connection is off without ever
        // running a core to serve it (the v2rayN-style proxy-only behaviour).
        SystemProxyModeRequested.Publish(type);
        await Task.CompletedTask;
    }

    public async Task ChangeSystemProxyAsync(ESysProxyType type, bool blChange)
    {
        // The explicit type is authoritative here. This matters when the dashboard
        // toggles the independent proxy while a connection policy temporarily owns
        // the effective capture mode.
        await SysProxyHandler.UpdateSysProxy(_config, false, type);

        BlSystemProxyClear = type == ESysProxyType.ForcedClear;
        BlSystemProxySet = type == ESysProxyType.ForcedChange;
        BlSystemProxyNothing = type == ESysProxyType.Unchanged;
        BlSystemProxyPac = type == ESysProxyType.Pac;

        if (blChange)
        {
            // Bildirim: StatusBarView yalnızca görünür legacy yüzeyde aktive olur;
            // WebView2 yerleşiminde dinleyici yoktur ve çağrı hiç yapılmaz.
            await DispatcherRefreshIconInteraction.TryHandleAsync(Unit.Default);
        }
    }

    public async Task RefreshRoutingsMenu()
    {
        var routings = await AppManager.Instance.RoutingItems();

        RoutingItems.Clear();
        RoutingItems.AddRange(routings);

        SelectedRouting = routings.FirstOrDefault(t => t.IsActive == true);
    }

    private async Task RoutingSelectedChangedAsync(bool c)
    {
        if (!c)
        {
            return;
        }

        if (SelectedRouting == null)
        {
            return;
        }

        var item = await AppManager.Instance.GetRoutingItem(SelectedRouting?.Id);
        if (item is null)
        {
            return;
        }

        if (await ConfigHandler.SetDefaultRouting(_config, item) == 0)
        {
            NoticeManager.Instance.SendMessageEx(ResUI.TipChangeRouting);
            ReloadRequested.Publish();
            // Bildirim: WebView2 yerleşiminde StatusBarView hiç yüklenmeyebilir ve
            // dinleyici kayıtlı olmayabilir — tepsi ikonu zaten kendi güncellemesini
            // ctor'da alır (bkz. ChangeSystemProxyAsync'teki aynı koruma).
            await DispatcherRefreshIconInteraction.TryHandleAsync(Unit.Default);
        }
    }

    private async Task DoSystemProxySelected(bool c)
    {
        if (!c)
        {
            return;
        }
        if (_config.SystemProxyItem.SysProxyType == (ESysProxyType)SystemProxySelected)
        {
            return;
        }
        await SetListenerType((ESysProxyType)SystemProxySelected);
    }

    private async Task DoEnableTun(bool c)
    {
        if (_config.TunModeItem.EnableTun == EnableTun)
        {
            return;
        }

        _config.TunModeItem.EnableTun = EnableTun;

        if (EnableTun && AllowEnableTun() == false)
        {
            // When running as a non-administrator, reboot to administrator mode
            if (Utils.IsWindows())
            {
                _config.TunModeItem.EnableTun = false;
                await AppManager.Instance.RebootAsAdmin();
                return;
            }
            else
            {
                // Linux/macOS şifre istemi hiçbir görünüme kayıtlı değil (WebView2
                // yerleşiminde StatusBarView aktive olmuyor) — istem yoksa geçişi
                // iptal et. TryHandleResultAsync bu durumda istisna atmaz.
                var (_, password) = await PasswordInputInteraction.TryHandleResultAsync(Unit.Default);
                if (password.IsNullOrEmpty())
                {
                    _config.TunModeItem.EnableTun = false;
                    return;
                }
            }
        }

        await ConfigHandler.SaveConfig(_config);
        ReloadRequested.Publish();
    }

    private bool AllowEnableTun()
    {
        if (Utils.IsWindows())
        {
            return Utils.IsAdministrator();
        }
        else if (Utils.IsLinux())
        {
            return AppManager.Instance.LinuxSudoPwd.IsNotEmpty();
        }
        else if (Utils.IsMacOS())
        {
            return AppManager.Instance.LinuxSudoPwd.IsNotEmpty();
        }
        return false;
    }

    #endregion System proxy and Routings

    #region UI

    public async Task InboundDisplayStatus()
    {
        StringBuilder sb = new();
        sb.Append($"[{EInboundProtocol.mixed}:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}");
        if (_config.Inbound.First().SecondLocalPortEnabled)
        {
            sb.Append($",{AppManager.Instance.GetLocalPort(EInboundProtocol.socks2)}");
        }
        sb.Append(']');
        InboundDisplay = $"{ResUI.LabLocal}:{sb}";

        if (_config.Inbound.First().AllowLANConn)
        {
            var lan = _config.Inbound.First().NewPort4LAN
                ? $"[{EInboundProtocol.mixed}:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks3)}]"
                : $"[{EInboundProtocol.mixed}:{AppManager.Instance.GetLocalPort(EInboundProtocol.socks)}]";
            InboundLanDisplay = $"{ResUI.LabLAN}:{lan}";
        }
        else
        {
            InboundLanDisplay = $"{ResUI.LabLAN}:{Global.None}";
        }
        await Task.CompletedTask;
    }

    public async Task UpdateStatistics(ServerSpeedItem update)
    {
        if (!_config.GuiItem.DisplayRealTimeSpeed)
        {
            return;
        }

        try
        {
            if (AppManager.Instance.IsRunningCore(ECoreType.mihomo))
            {
                SpeedProxyDisplay = string.Format(ResUI.SpeedDisplayText, EInboundProtocol.mixed, Utils.HumanFy(update.ProxyUp), Utils.HumanFy(update.ProxyDown));
                SpeedDirectDisplay = string.Empty;
            }
            else
            {
                SpeedProxyDisplay = string.Format(ResUI.SpeedDisplayText, Global.ProxyTag, Utils.HumanFy(update.ProxyUp), Utils.HumanFy(update.ProxyDown));
                SpeedDirectDisplay = string.Format(ResUI.SpeedDisplayText, Global.DirectTag, Utils.HumanFy(update.DirectUp), Utils.HumanFy(update.DirectDown));
            }
        }
        catch
        {
        }
        await Task.CompletedTask;
    }

    #endregion UI
}
