using System.Reactive.Concurrency;
using ServiceLib.Services.Gpn;

namespace ServiceLib.ViewModels;

public class MainWindowViewModel : MyReactiveObject
{
    public Interaction<Unit, string?> ReadTextFromClipboardInteraction { get; } = new();
    public Interaction<Unit, byte[]?> ScanScreenInteraction { get; } = new();
    public Interaction<Unit, string?> BrowseImageFileInteraction { get; } = new();
    public Interaction<bool?, Unit> ShowHideWindowInteraction { get; } = new();

    public bool DesignMode { get; set; }

    public ProfilesViewModel ProfilesViewModel { get; } = new();
    public MsgViewModel MsgViewModel { get; } = new();
    public ClashProxiesViewModel ClashProxiesViewModel { get; } = new();
    public ClashConnectionsViewModel ClashConnectionsViewModel { get; } = new();
    public SplitTunnelViewModel ConnectionViewModel { get; } = new();
    public CheckUpdateViewModel CheckUpdateViewModel { get; } = new();
    public BackupAndRestoreViewModel BackupAndRestoreViewModel { get; } = new();
    public StatusBarViewModel StatusBarViewModel { get; } = StatusBarViewModel.Instance;

    #region Menu

    //servers
    public ReactiveCommand<Unit, Unit> AddVmessServerCmd { get; }

    public ReactiveCommand<Unit, Unit> AddVlessServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddShadowsocksServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddSocksServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddHttpServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddTrojanServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddHysteria2ServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddTuicServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddWireguardServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddAnytlsServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddNaiveServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddCustomServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddPolicyGroupServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddProxyChainServerCmd { get; }
    public ReactiveCommand<Unit, Unit> AddServerViaClipboardCmd { get; }
    public ReactiveCommand<Unit, Unit> AddServerViaScanCmd { get; }
    public ReactiveCommand<Unit, Unit> AddServerViaImageCmd { get; }

    //Subscription
    public ReactiveCommand<Unit, Unit> SubSettingCmd { get; }

    public ReactiveCommand<Unit, Unit> SubUpdateCmd { get; }
    public ReactiveCommand<Unit, Unit> SubUpdateViaProxyCmd { get; }
    public ReactiveCommand<Unit, Unit> SubGroupUpdateCmd { get; }
    public ReactiveCommand<Unit, Unit> SubGroupUpdateViaProxyCmd { get; }

    //Setting
    public ReactiveCommand<Unit, Unit> OptionSettingCmd { get; }

    public ReactiveCommand<Unit, Unit> RoutingSettingCmd { get; }
    public ReactiveCommand<Unit, Unit> DNSSettingCmd { get; }
    public ReactiveCommand<Unit, Unit> FullConfigTemplateCmd { get; }
    public ReactiveCommand<Unit, Unit> GlobalHotkeySettingCmd { get; }
    public ReactiveCommand<Unit, Unit> RebootAsAdminCmd { get; }
    public ReactiveCommand<Unit, Unit> ClearServerStatisticsCmd { get; }
    public ReactiveCommand<Unit, Unit> OpenTheFileLocationCmd { get; }

    //Presets
    public ReactiveCommand<Unit, Unit> RegionalPresetDefaultCmd { get; }

    public ReactiveCommand<Unit, Unit> RegionalPresetRussiaCmd { get; }

    public ReactiveCommand<Unit, Unit> RegionalPresetIranCmd { get; }

    public ReactiveCommand<Unit, Unit> ReloadCmd { get; }

    [Reactive]
    public bool BlReloadEnabled { get; set; }

    // Belirgin "GPN Bağlan" butonu — seçili profil WireGuard dışı olsa da (ör.
    // VLESS/SS seçiliyken), kullanıcı açıkça GPN modunu seçtiyse İtalya/Almanya
    // otomatik seçimini zorla tetikler.
    public ReactiveCommand<Unit, Unit> GpnConnectCmd { get; }

    [Reactive]
    public bool ShowClashUI { get; set; }

    [Reactive]
    public int TabMainSelectedIndex { get; set; }

    [Reactive] public bool BlIsWindows { get; set; }

    [Reactive] public bool BlNewUpdate { get; set; }

    [Reactive] public EGirdOrientation MainGirdOrientation { get; set; }

    #endregion Menu

    #region Init

    public MainWindowViewModel()
    {
        _config = AppManager.Instance.Config;
        BlIsWindows = Utils.IsWindows();
        MainGirdOrientation = _config.UiItem.MainGirdOrientation;
        // Open on the unified connection dashboard (mode + monitoring + app routing).
        TabMainSelectedIndex = 4;

        #region WhenAnyValue && ReactiveCommand

        //servers
        AddVmessServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.VMess);
        });
        AddVlessServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.VLESS);
        });
        AddShadowsocksServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.Shadowsocks);
        });
        AddSocksServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.SOCKS);
        });
        AddHttpServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.HTTP);
        });
        AddTrojanServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.Trojan);
        });
        AddHysteria2ServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.Hysteria2);
        });
        AddTuicServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.TUIC);
        });
        AddWireguardServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.WireGuard);
        });
        AddAnytlsServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.Anytls);
        });
        AddNaiveServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.Naive);
        });
        AddCustomServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.Custom);
        });
        AddPolicyGroupServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.PolicyGroup);
        });
        AddProxyChainServerCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerAsync(EConfigType.ProxyChain);
        });
        AddServerViaClipboardCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerViaClipboardAsync(null);
        });
        AddServerViaScanCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerViaScanAsync();
        });
        AddServerViaImageCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AddServerViaImageAsync();
        });

        //Subscription
        SubSettingCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await SubSettingAsync();
        });

        SubUpdateCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await UpdateSubscriptionProcess("", false);
        });
        SubUpdateViaProxyCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await UpdateSubscriptionProcess("", true);
        });
        SubGroupUpdateCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await UpdateSubscriptionProcess(_config.SubIndexId, false);
        });
        SubGroupUpdateViaProxyCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await UpdateSubscriptionProcess(_config.SubIndexId, true);
        });

        //Setting
        OptionSettingCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await OptionSettingAsync();
        });
        RoutingSettingCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await RoutingSettingAsync();
        });
        DNSSettingCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await DNSSettingAsync();
        });
        FullConfigTemplateCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await FullConfigTemplateAsync();
        });
        GlobalHotkeySettingCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            var globalHotkeySettingViewModel = new GlobalHotkeySettingViewModel();
            if (await AppManager.Instance.WindowDialog.ShowDialogAsync(globalHotkeySettingViewModel) == true)
            {
                NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
            }
        });
        RebootAsAdminCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await AppManager.Instance.RebootAsAdmin();
        });
        ClearServerStatisticsCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ClearServerStatistics();
        });
        OpenTheFileLocationCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await OpenTheFileLocation();
        });

        ReloadCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await Reload();
        });

        GpnConnectCmd = ReactiveCommand.CreateFromTask(async () => await GpnConnectAsync());

        RegionalPresetDefaultCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ApplyRegionalPreset(EPresetType.Default);
        });

        RegionalPresetRussiaCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ApplyRegionalPreset(EPresetType.Russia);
        });

        RegionalPresetIranCmd = ReactiveCommand.CreateFromTask(async () =>
        {
            await ApplyRegionalPreset(EPresetType.Iran);
        });

        #endregion WhenAnyValue && ReactiveCommand

        #region AppEvents

        AppEvents.AddServerViaClipboardRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ => await AddServerViaClipboardAsync(null));

        AppEvents.HasUpdateNotified
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async bl => BlNewUpdate = bl);

        #endregion AppEvents

        ProfilesViewModel.RefreshServersRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ => await RefreshServers());

        var vmReloadRequestedList = new List<IObservable<Unit>>
        {
            ProfilesViewModel.ReloadRequested.AsObservable(),
            StatusBarViewModel.ReloadRequested.AsObservable(),
            CheckUpdateViewModel.ReloadRequested.AsObservable(),
        };

        foreach (var reloadRequested in vmReloadRequestedList)
        {
            reloadRequested
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(async _ => await Reload());
        }

        StatusBarViewModel.AddServerViaScanRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ => await AddServerViaScanAsync());

        StatusBarViewModel.AddServerViaClipboardRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ => await AddServerViaClipboardAsync(null));

        StatusBarViewModel.ShowHideWindowRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async blShow =>
            {
                // Bildirim: pencere henüz yüklenmediyse (WhenActivated handler'ları
                // kayıtlı değilken tepsi/arka plan isteği geldi) istek sessizce düşer.
                // TryHandleAsync dinleyici yoksa HİÇ ÇAĞIRMAZ: eskiden burada
                // UnhandledInteractionException atılıp yutuluyordu ve Visual Studio
                // her çağrı için ilk şans istisnası satırı yazıyordu.
                await ShowHideWindowInteraction.TryHandleAsync(blShow);
            });

        StatusBarViewModel.SetDefaultServerRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async indexId => await ProfilesViewModel.SetDefaultServer(indexId));

        StatusBarViewModel.SubscriptionsUpdateRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async blProxy => await UpdateSubscriptionProcess("", blProxy));

        _ = Init();
    }

    private async Task Init()
    {
        AppManager.Instance.ShowInTaskbar = true;

        if (DesignMode)
        {
            return;
        }

        //await ConfigHandler.InitBuiltinRouting(_config);
        await ConfigHandler.InitBuiltinDNS(_config);
        await ConfigHandler.InitBuiltinFullConfigTemplate(_config);
        await ProfileExManager.Instance.Init();
        AppManager.Instance.CoreEngineHost ??= new CoreEngineHost(_config, UpdateHandler);
        await AppManager.Instance.CoreEngineHost.InitializeAsync();
        await CertPemManager.Instance.Init(_config);
        // Bakım tikleri (abonelik/çekirdek güncelleme) bağlantı uçuştayken ERTELENİR:
        // bu tikler Reload() çağırabilir ve Reload'ın bağlı bir tünelde ağır iş
        // koşturması maç ortasında kesinti üretirdi (bkz. ShouldDeferScheduledTasks).
        TaskManager.Instance.RegUpdateTask(_config, UpdateTaskHandler, ShouldDeferScheduledTasks);

        if (_config.GuiItem.EnableStatistics
            || _config.GuiItem.DisplayRealTimeSpeed
            || _config.GuiItem.EnableDashboardTelemetry)
        {
            await StatisticsManager.Instance.Init(_config, UpdateStatisticsHandler);
        }
        await RefreshServersDispatcherAsync();

        if (Utils.IsWindows() && !Utils.IsAdministrator() && _config.TunModeItem.EnableTun)
        {
            NoticeManager.Instance.Enqueue(ResUI.ConnectionNeedAdmin);
        }

        // Tell the user once when the config was recovered from the backup or reset.
        if (ConfigHandler.LoadWarning.IsNotEmpty())
        {
            NoticeManager.Instance.Enqueue(ConfigHandler.LoadWarning);
            ConfigHandler.LoadWarning = null;
        }

        await Reload();
    }

    #endregion Init

    #region Actions

    private async Task UpdateHandler(bool notify, string msg)
    {
        NoticeManager.Instance.SendMessage(msg);
        if (notify)
        {
            NoticeManager.Instance.Enqueue(msg);
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Zamanlanmış bakım tikleri ertelenmeli mi? (Saf, test edilebilir.)
    ///
    /// TRUE iken abonelik otomatik güncellemesi ve çekirdek güncelleme kontrolü
    /// bu turu atlar. İki koruma var:
    ///   * <see cref="GpnConnectionState.Connecting"/> — bağlanma uçuştayken
    ///     bakım işi başlatmak ölçümü ve el sıkışmayı yavaşlatır,
    ///   * tünel yeni kurulduysa (<see cref="GpnConnectFreshWindow"/>) — ilk
    ///     saniyeler en kırılgan andır: adaptör/rota yerleşiyor, DNS fake-ip
    ///     eşlemesi ısınıyor. Bu pencerede Reload tabanlı bakım çalıştırılmaz.
    ///
    /// Erteleme kayıp DEĞİLDİR: süresi gelmiş bir abonelik bir sonraki dakikada
    /// yeniden denenir; güncelleme kontrolü 24 saatlik periyodunu korur.
    /// </summary>
    internal static readonly TimeSpan GpnConnectFreshWindow = TimeSpan.FromSeconds(60);

    internal static bool ShouldDeferScheduledTasks(
        GpnConnectionState? state, DateTimeOffset? updatedAt, DateTimeOffset now)
    {
        if (state is GpnConnectionState.Connecting)
        {
            return true;
        }

        if (state is GpnConnectionState.Connected && updatedAt is { } at)
        {
            return now - at < GpnConnectFreshWindow;
        }

        return false;
    }

    private bool ShouldDeferScheduledTasks()
    {
        var snapshot = _gpnCoordinator?.Snapshot;
        return ShouldDeferScheduledTasks(snapshot?.State, snapshot?.UpdatedAt, DateTimeOffset.UtcNow);
    }

    private async Task UpdateTaskHandler(bool success, string msg)
    {
        NoticeManager.Instance.SendMessageEx(msg);
        if (success)
        {
            var indexIdOld = _config.IndexId;
            await RefreshServersDispatcherAsync();

            // If indexId changed or subIndexId is empty, directly reload.
            if (indexIdOld != _config.IndexId || _config.SubIndexId.IsNullOrEmpty())
            {
                await Reload();
            }
            else
            {
                // The activity config belongs to the current group.
                var profile = await AppManager.Instance.GetProfileItem(_config.IndexId);
                if (profile != null && profile.Subid == _config.SubIndexId)
                {
                    await Reload();
                }
            }

            if (_config.UiItem.EnableAutoAdjustMainLvColWidth)
            {
                await ProfilesViewModel.AdjustMainLvColWidth();
            }
        }
    }

    private async Task UpdateStatisticsHandler(ServerSpeedItem update)
    {
        if (!AppManager.Instance.ShowInTaskbar)
        {
            return;
        }
        AppEvents.DispatcherStatisticsRequested.Publish(update);
        await Task.CompletedTask;
    }

    #endregion Actions

    #region Servers && Groups

    private async Task RefreshServers()
    {
        await ProfilesViewModel.RefreshServersBiz();
        await StatusBarViewModel.RefreshServersBiz();

        // await Task.Delay(200);
    }

    private async Task RefreshServersDispatcherAsync()
    {
        await Observable.Start(async () => await RefreshServers(), RxSchedulers.MainThreadScheduler);
    }

    private async Task RefreshSubscriptions()
    {
        await Observable.Start(async () => await ProfilesViewModel.RefreshSubscriptions(), RxSchedulers.MainThreadScheduler);
    }

    #endregion Servers && Groups

    #region Add Servers

    public async Task AddServerAsync(EConfigType eConfigType)
    {
        ProfileItem item = new()
        {
            Subid = _config.SubIndexId,
            ConfigType = eConfigType,
            IsSub = false,
        };

        bool? ret = false;
        if (eConfigType == EConfigType.Custom)
        {
            var addServer2ViewModel = new AddServer2ViewModel(item);
            ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(addServer2ViewModel);
        }
        else if (eConfigType.IsGroupType())
        {
            var addGroupServerViewModel = new AddGroupServerViewModel(item);
            ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(addGroupServerViewModel);
        }
        else
        {
            var addServerViewModel = new AddServerViewModel(item);
            ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(addServerViewModel);
        }
        if (ret == true)
        {
            await RefreshServersDispatcherAsync();
            if (item.IndexId == _config.IndexId)
            {
                await Reload();
            }
        }
    }

    public async Task AddServerViaClipboardAsync(string? clipboardData)
    {
        var stringData = clipboardData;
        if (clipboardData == null)
        {
            var result = await ReadTextFromClipboardInteraction.Handle(Unit.Default);
            if (result.IsNullOrEmpty())
            {
                NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
                return;
            }
            stringData = result;
        }
        var ret = await ConfigHandler.AddBatchServers(_config, stringData, _config.SubIndexId, false);
        if (ret > 0)
        {
            await RefreshSubscriptions();
            await RefreshServersDispatcherAsync();
            NoticeManager.Instance.Enqueue(string.Format(ResUI.SuccessfullyImportedServerViaClipboard, ret));
        }
        else
        {
            NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
        }
    }

    public async Task AddServerViaScanAsync()
    {
        var result = await ScanScreenInteraction.Handle(Unit.Default);
        await ScanScreenResult(result);
    }

    public async Task ScanScreenResult(byte[]? bytes)
    {
        var result = QRCodeUtils.ParseBarcode(bytes);
        await AddScanResultAsync(result);
    }

    public async Task AddServerViaImageAsync()
    {
        var imageFileName = await BrowseImageFileInteraction.Handle(Unit.Default);
        await AddScanResultAsync(imageFileName);
    }

    public async Task ScanImageResult(string fileName)
    {
        if (fileName.IsNullOrEmpty())
        {
            return;
        }

        var result = QRCodeUtils.ParseBarcode(fileName);
        await AddScanResultAsync(result);
    }

    private async Task AddScanResultAsync(string? result)
    {
        if (result.IsNullOrEmpty())
        {
            NoticeManager.Instance.Enqueue(ResUI.NoValidQRcodeFound);
        }
        else
        {
            var ret = await ConfigHandler.AddBatchServers(_config, result, _config.SubIndexId, false);
            if (ret > 0)
            {
                await RefreshSubscriptions();
                await RefreshServersDispatcherAsync();
                NoticeManager.Instance.Enqueue(ResUI.SuccessfullyImportedServerViaScan);
            }
            else
            {
                NoticeManager.Instance.Enqueue(ResUI.OperationFailed);
            }
        }
    }

    #endregion Add Servers

    #region Subscription

    private async Task SubSettingAsync()
    {
        var subSettingViewModel = new SubSettingViewModel();
        if (await AppManager.Instance.WindowDialog.ShowDialogAsync(subSettingViewModel) == true)
        {
            await RefreshSubscriptions();
        }
    }

    public async Task UpdateSubscriptionProcess(string subId, bool blProxy)
    {
        await Task.Run(async () => await SubscriptionHandler.UpdateProcess(_config, subId, blProxy, UpdateTaskHandler));
    }

    #endregion Subscription

    #region Setting

    private async Task OptionSettingAsync()
    {
        var settingViewModel = new OptionSettingViewModel();
        var ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(settingViewModel);
        if (ret == true)
        {
            MainGirdOrientation = _config.UiItem.MainGirdOrientation;
            RxSchedulers.MainThreadScheduler.Schedule(async () =>
            {
                await StatusBarViewModel.InboundDisplayStatus();
            });
            await Reload();
        }
    }

    private async Task RoutingSettingAsync()
    {
        var routingSettingViewModel = new RoutingSettingViewModel();
        var ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(routingSettingViewModel);
        if (ret == true)
        {
            await ConfigHandler.InitBuiltinRouting(_config);
            RxSchedulers.MainThreadScheduler.Schedule(async () =>
            {
                await StatusBarViewModel.RefreshRoutingsMenu();
            });
            await Reload();
        }
    }

    private async Task DNSSettingAsync()
    {
        var dnsSettingViewModel = new DNSSettingViewModel();
        var ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(dnsSettingViewModel);
        if (ret == true)
        {
            await Reload();
        }
    }

    private async Task FullConfigTemplateAsync()
    {
        var fullConfigTemplateViewModel = new FullConfigTemplateViewModel();
        var ret = await AppManager.Instance.WindowDialog.ShowDialogAsync(fullConfigTemplateViewModel);
        if (ret == true)
        {
            await Reload();
        }
    }

    private async Task ClearServerStatistics()
    {
        await StatisticsManager.Instance.ClearAllServerStatistics();
        await RefreshServersDispatcherAsync();
    }

    private async Task OpenTheFileLocation()
    {
        var path = Utils.StartupPath();
        if (Utils.IsWindows())
        {
            ProcUtils.ProcessStart(path);
        }
        else if (Utils.IsLinux())
        {
            ProcUtils.ProcessStart("xdg-open", path);
        }
        else if (Utils.IsMacOS())
        {
            ProcUtils.ProcessStart("open", path);
        }
        await Task.CompletedTask;
    }

    #endregion Setting

    #region core job

    private bool _hasNextReloadJob = false;
    private readonly SemaphoreSlim _reloadSemaphore = new(1, 1);

    // GPN "Bağlan" orkestratörü — seçili profil bir WireGuard profiliyse İtalya/Almanya
    // adaylarını ölçüp (ICMP → UDP el sıkışma → mod kararı) en düşük gecikmeli sunucuya
    // bağlar + failover izleyicisini çalıştırır. Tek örnek: failover izleyici durumu
    // ve BehaviorSubject (GpnConnectionSnapshot) süreç boyunca korunur.
    private IGpnConnectionCoordinator? _gpnCoordinator;

    private IGpnConnectionCoordinator GetGpnCoordinator()
    {
        _gpnCoordinator ??= new GpnConnectionCoordinator(
            GetGpnSelectionService(),
            new GpnCoreLauncher(_config, UpdateHandler, captureBridge: GetGpnCaptureBridge()));
        return _gpnCoordinator;
    }

    // Faz 2b/2c yakalama tünel köprüsü — bağlan akışında GpnCaptureLoop'un
    // varsayılan tüketimi yerine WireGuardTunnelService.CreateInjectHandler ile
    // paketleri tünele şifreleyip Wintun adaptörüne enjekte eder. Hedef adlar
    // (vpn eylemli oyunlar) SplitTunnelViewModel'den canlı okunur; köprü yalnızca
    // oyun çalışıyorsa canlıya alınır (GpnCoreLauncher içinde).
    private GpnCaptureBridge? _gpnCaptureBridge;

    private GpnCaptureBridge GetGpnCaptureBridge()
    {
        _gpnCaptureBridge ??= new GpnCaptureBridge(
            GetGpnTargetNames,
            new WinDivertEngine(),
            new WireGuardTunnelService(),
            new GpnCaptureSettingsProvider(),
            onFailure: async reason =>
            {
                // Köprü gerçek bir hatayla başlatılamadı (Wintun açılışı, geçersiz
                // anahtarlar, tarama hatası vb.) — sessizce V2rayTCP'ye düşmek yerine
                // nedeni snackbar/toast olarak göster (ayrıntı için Logs\<tarih>.txt).
                var message = $"GPN oyun tüneli başlatılamadı — V2rayTCP düşüşü kullanılıyor. Neden: {reason}";
                DiagLog.Write($"GPN_BRIDGE notify: {reason}");
                // Köprü gerçek bir hatayla düştü — WinDivert çevre durumunu tazele
                // (DLL/sürücü eksikse dashboard banner'ı + diag satırı düşer).
                // attemptInstall: false — kurulum açılışta denenmiştir; burada yalnızca rapor.
                WinDivertHealthMonitor.Instance.Check(attemptInstall: false);
                RxSchedulers.MainThreadScheduler.Schedule(() =>
                {
                    NoticeManager.Instance.Enqueue(message);
                    NoticeManager.Instance.SendMessageEx(message);
                });
            });
        // Tier 1 — in-process motor: NativeGpnStartStrategy köprüyü buradan çözer
        // (NativeGpnEnginePolicy.IsEnabled açıkken CoreManager yerel motoru seçer).
        AppManager.Instance.CaptureBridge = _gpnCaptureBridge;
        return _gpnCaptureBridge;
    }

    /// <summary>
    /// Tünellenecek hedef exe adları — Game Boost listesindeki "vpn" eylemli
    /// uygulamalar (GpnTargetResolverBridge ile aynı kaynak/kural).
    /// </summary>
    private IReadOnlyList<string> GetGpnTargetNames()
        => GpnTargetResolverBridge.ExtractTargetNames(ConnectionViewModel.Apps, ConnectionViewModel.InvertManualRouting);

    // Seçim ölçümleri ve dashboard "sunucu kümesi" kartı için tek, önbellekli
    // GpnServerSelectionService örneği — koordinatörle aynı örneği paylaşır.
    private GpnServerSelectionService? _gpnSelection;

    private GpnServerSelectionService GetGpnSelectionService()
    {
        _gpnSelection ??= new GpnServerSelectionService();
        return _gpnSelection;
    }

    /// <summary>
    /// Dashboard'daki İtalya/Almanya "sunucu kümesi" kartı için canlı gecikme ölçümü.
    /// Ölçüm yalnızca okuma amaçlıdır (tünel başlatılmaz); <see cref="IGpnServerSelectionService.ProbeAllAsync"/>
    /// üzerinden paralel ICMP ping → gecikme/kayıp döndürür. UDP sağlık kontrolü
    /// host tarafında (MainWindow) ayrıca yürütülür.
    /// </summary>
    public Task<IReadOnlyList<GpnServerProbeResult>> ProbeServersAsync(
        IReadOnlyList<GpnServerProfile> servers,
        GpnProbeOptions? options = null,
        CancellationToken cancellationToken = default)
        => GetGpnSelectionService().ProbeAllAsync(servers, options, cancellationToken);

    /// <summary>
    /// Açılış ön yüklemesi (Faz 3): GPN sunucu ölçüm önbelleğini ISITIR.
    ///
    /// Bağlanma yolunun yapacağı ICMP + UDP el sıkışma ölçümleri burada, kullanıcı
    /// bir şey beklemediği anda ve paralel olarak yapılır; sonraki "Bağlan" ölçümü
    /// (kısa ömürlü önbellek sayesinde) büyük ölçüde hazır veriden karşılanır.
    /// Best-effort'tur: aday yoksa veya ölçüm başarısız olursa sessizce çıkar —
    /// ön yükleme asla bağlanmayı veya açılışı bozmaz.
    /// </summary>
    public async Task WarmGpnProbeCacheAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var candidates = await WireGuardServerCatalog.LoadAsync().ConfigureAwait(false);
            if (candidates.Count == 0)
            {
                return;
            }

            // Zaman aşımları kullanıcı ayarından (bağlanma yoluyla aynı kaynak) gelir;
            // böylece ön yükleme ölçümü bağlanmanın kullanacağı ölçümle birebir aynı
            // anahtara sahip olur ve önbellekten gerçekten karşılanır.
            var probe = _config.GuiItem.GpnProbe ?? new GpnProbeTuning();
            var options = new GpnProbeOptions
            {
                UseCache = true,
                PerSampleTimeoutMs = Math.Max(200, probe.IcmpSampleTimeoutMs),
                UdpCheck = new UdpHealthCheckOptions(WaitTimeoutMs: Math.Max(200, probe.UdpWaitTimeoutMs)),
                HandshakeProbe = new WireGuardHandshakeProbeOptions(
                    WaitTimeoutMs: Math.Max(300, probe.HandshakeWaitTimeoutMs),
                    MaxAttempts: 2),
            };

            DiagLog.Write($"GPN_PREFLIGHT probe-warm start candidates={candidates.Count}");
            await GetGpnSelectionService().WarmProbeCacheAsync(candidates, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Açılış iptal edildi — sessizce çık.
        }
        catch (Exception ex)
        {
            Logging.SaveLog("[GPN] Ön yükleme ölçümü hatası", ex);
        }
    }

    /// <summary>
    /// Dashboard ⚡ Test paneli için paralel UDP sağlık testi (el sıkışma + junk
    /// zinciri) — seçim/failover ile AYNI kanıt; UDP rozeti ve gecikme fallback'i
    /// (Open round-trip) bu sonuçlardan beslenir.
    /// </summary>
    public Task<IReadOnlyList<UdpProbeResult>> ProbeUdpAllAsync(
        IReadOnlyList<GpnServerProfile> servers,
        GpnProbeOptions? options = null,
        CancellationToken cancellationToken = default)
        => GetGpnSelectionService().ProbeUdpAllAsync(servers, options, cancellationToken);

    /// <summary>
    /// Şu an bağlı (veya bağlanmakta olan) GPN sunucusu — yoksa null. Failover
    /// matrisinin "aktif" satırını işaretlemek için kullanılır.
    /// </summary>
    public GpnServerProfile? CurrentGpnServer => _gpnCoordinator?.Snapshot.Server;

    /// <summary>GPN koordinatörünün anlık durumu (bağlantı hata kartı için).</summary>
    public GpnConnectionSnapshot? GpnCoordinatorSnapshot => _gpnCoordinator?.Snapshot;

    /// <summary>
    /// Dashboard "failover matrisi" kartı için: aynı ölçümü varsayılan + katı
    /// politika altında değerlendiren karar matrisi. Saf hesaplama — tünel başlatılmaz.
    /// </summary>
    public GpnFailoverMatrix EvaluateFailoverMatrix(
        GpnServerProfile active,
        IReadOnlyList<GpnServerProfile> candidates,
        IReadOnlyList<GpnServerProbeResult> pingResults,
        IReadOnlyDictionary<string, UdpProbeResult> udpResults,
        GpnProbeOptions? options = null)
        => GetGpnSelectionService().EvaluateFailoverMatrix(active, candidates, pingResults, udpResults, options);

    /// <summary>
    /// Dashboard "en iyi aday" kartı için: GPN Bağlan'ın yapacağı otomatik seçim
    /// kararını önceden gösterir. Saf hesaplama (tünel başlatılmaz) —
    /// <see cref="SelectBestServerAsync"/> ile birebir aynı mantık.
    /// </summary>
    public GpnSelectionPrediction EvaluateSelection(
        IReadOnlyList<GpnServerProfile> servers,
        IReadOnlyList<GpnServerProbeResult> pingResults,
        IReadOnlyDictionary<string, UdpProbeResult> udpResults,
        GpnProbeOptions? options = null)
        => GetGpnSelectionService().EvaluateSelection(servers, pingResults, udpResults, options);

    /// <summary>
    /// GPN akışının çalışıp çalışmayacağına karar verir (saf — test edilebilir):
    /// kullanıcının kalıcı modu Manuel (GPN) ise ve TUN etkinse GPN akışı; aksi halde
    /// normal tek-profil akışı. Seçili profilin türü kararı ETKİLEMEZ — kullanıcının
    /// GPN/VPN seçimi her zaman önceliklidir.
    /// </summary>
    internal static bool ShouldRunGpnFlow(int userMode, bool tunEnabled)
        => userMode == SplitTunnelViewModel.ModeManual && tunEnabled;

    /// <summary>
    /// Kullanıcı GPN akışını (Manuel mod + TUN) yapılandırmış mı? Açılış ön
    /// yüklemesinin GPN sunucu ölçümlerini ısıtıp ısıtmayacağına karar verir —
    /// GPN kapalıyken adaylara boşa ICMP/el sıkışma paketi gönderilmez.
    ///
    /// Karar <see cref="ShouldRunGpnFlow"/> ile AYNI kaynaktan üretilir; böylece
    /// ön yükleme ile gerçek bağlanma akışı asla ayrışmaz.
    /// </summary>
    public bool IsGpnFlowConfigured()
        => ShouldRunGpnFlow(
            _config.ConnectionItem?.Mode ?? SplitTunnelViewModel.ModeOff,
            _config.TunModeItem.EnableTun);

    /// <summary>
    /// GPN otomatik-seçimi artık uygulanmıyorsa (ör. kullanıcı VPN moduna geçtiğinde
    /// veya TUN kapandığında <paramref name="gpnConnected"/> = false olur) aktif GPN
    /// koordinatörünü teardown eder: failover izleyicisini iptal eder ve launcher'ın
    /// başlattığı tüneli durdurur. Aksi halde eski failover döngüsü arka planda yaşar ve
    /// normal (tek-profil) akışla çakışır.
    ///
    /// Test kolaylığı için static ve internal: ağır UI bağımlılıkları olan kurucu
    /// gerektirmeden GPN→VLESS/SS geçiş kararını deterministik olarak doğrulanabilir.
    /// </summary>
    internal static async Task StopGpnCoordinatorWhenNotConnectedAsync(
        bool gpnConnected,
        IGpnConnectionCoordinator? coordinator = null)
    {
        if (!gpnConnected && coordinator is not null)
        {
            await coordinator.DisconnectAsync(CancellationToken.None);
            Logging.SaveLog("[GPN] Koordinatör durduruldu — normal akışa geçiliyor.");
        }
    }

    /// <summary>
    /// Aktif GPN koordinatör tünelini durdurur: failover izleyicisi iptal edilir,
    /// yakalama köprüsü/çekirdek durdurulur ve superset oturum kaydı temizlenir.
    /// İki yerde çağrılır:
    ///
    ///  1) Kullanıcı bağlantıyı KESTİĞİNDE (ModeOff) — yumuşak uygulayıcı Off
    ///     vektörünü canlı mihomo'ya yazar (trafik DIRECT) ama Wintun/TUN adaptörünü,
    ///     failover izleyicisini ve superset oturumu yerinde bırakır; "Bağlantıyı Kes"
    ///     gerçek bir teardown olmalı (aksi halde tünel gizlice ayakta kalır ve sonraki
    ///     bağlantılar port/TUN çakışması yaşar — "hemen kesilmiyor" hissinin kaynağı),
    ///  2) Uygulama kapanışında — düzenli teardown olmadan çıkış, arka planda izleyici
    ///     görevi ve superset oturum bırakır (kararsız kapanışın kaynağı).
    ///
    /// Koordinatör kurulmamış veya zaten kopuksa no-op.
    /// </summary>
    public async Task StopGpnTunnelIfActiveAsync()
        => await StopGpnTunnelIfActiveAsync(_gpnCoordinator);

    /// <summary>
    /// <see cref="StopGpnTunnelIfActiveAsync"/>'in test edilebilir hali: koordinatör
    /// bağlı/bağlanıyor durumundaysa teardown eder, aksi halde hiçbir şey yapmaz.
    /// </summary>
    internal static async Task StopGpnTunnelIfActiveAsync(IGpnConnectionCoordinator? coordinator)
    {
        if (coordinator is null)
        {
            return;
        }
        var snapshot = coordinator.Snapshot;
        if (snapshot.State is GpnConnectionState.Connected or GpnConnectionState.Connecting)
        {
            await coordinator.DisconnectAsync(CancellationToken.None);
            Logging.SaveLog("[GPN] Koordinatör durduruldu — kullanıcı bağlantıyı kesti / uygulama kapanıyor.");
        }
    }

    /// <summary>
    /// WireGuard profilindeyken GPN otomatik-seçim akışını dener. Aday yoksa false
    /// döner ki normal (tek profil) bağlantı akışı devreye girsin.
    /// </summary>
    private async Task<bool> TryRunGpnConnectAsync()
    {
        var candidates = await WireGuardServerCatalog.LoadAsync();
        if (candidates.Count == 0)
        {
            Logging.SaveLog("[GPN] WireGuard adayı bulunamadı — normal akışa düşülüyor.");
            return false;
        }

        // Kullanıcının seçtiği profil (dashboard node seçici → default server) bir GPN
        // WireGuard sunucusuyla eşleşiyorsa bağlantıda o sunucu TERCİH edilir: otomatik
        // ölçüm yerine seçili sunucu denenir; yalnızca o sunucu başarısızsa Akıllı
        // Düşüş tüm adayları ölçerek devreye girer. Seçili profil GPN dışı bir düğümse
        // (VLESS/SS vb.) tercih yoktur → tam otomatik ölçüm (eski davranış).
        GpnServerProfile? preferred = null;
        var selectedProfile = await ConfigHandler.GetDefaultServer(_config).ConfigureAwait(false);
        if (selectedProfile?.ConfigType == EConfigType.WireGuard
            && WireGuardServerCatalog.TryMap(selectedProfile, out var mappedSelected))
        {
            // Eşleşme uç noktaya (host:port) göre yapılır çünkü TryMap(ProfileItem)
            // IndexId'yi ServerId olarak atar (UUID), ama gpn_servers tablosundaki
            // adaylar StableServerId formatındadır ("92.4.220.236:51820").
            // EndpointHost + EndpointPort her iki taraf için de güvenilir eşleşmedir.
            preferred = candidates.FirstOrDefault(c =>
                string.Equals(c.EndpointHost, mappedSelected.EndpointHost, StringComparison.OrdinalIgnoreCase)
                && c.EndpointPort == mappedSelected.EndpointPort);
        }
        if (preferred is not null)
        {
            Logging.SaveLog($"[GPN] Seçili sunucu tercih ediliyor: {preferred.Name} ({preferred.ServerId})");
        }

        try
        {
            var coordinator = GetGpnCoordinator();
            var probe = _config.GuiItem.GpnProbe ?? new GpnProbeTuning();
            var snapshot = await coordinator.ConnectAsync(candidates,
                options: new GpnProbeOptions
                {
                    // Kısa ömürlü ölçüm önbelleği (Faz 2): kullanıcı "kes → bağlan"
                    // yaptığında veya Reload tetiklendiğinde aynı sunucuların
                    // ICMP/el sıkışma ölçümleri saniyeler içinde baştan yapılmasın.
                    // Ölçüm-kritik yollar (dashboard ⚡ Test ve failover izleyicisi)
                    // bu bayrağı AÇMAZ — ikisi de her zaman taze ölçüm ister.
                    UseCache = true,

                    // V2rayTCP düşüşü sonrası otomatik Tier-2 (WireGuard) kurtarma
                    // kullanıcı ayarından gelir (GUIItem.GpnEnableRecoveryWatch).
                    EnableRecoveryWatch = _config.GuiItem.GpnEnableRecoveryWatch,

                    // GPN otomatik sunucu değiştirmesi (failover/ping-pong) kullanıcı
                    // ayarından gelir. Varsayılan FALSE → stabil/Kesintisiz mod: seçilen
                    // sunucuya takılı kalınır, otomatik geçiş/V2rayTCP düşüşü/kurtarma kapalı.
                    EnableFailover = _config.GuiItem.GpnEnableFailover,

                    // Zaman aşımları ve yüksek-gecikme toleransı artık config'den
                    // (GUIItem.GpnProbe) ayarlanabilir — varsayılanlarla birebir aynı
                    // başlar; uzak/yoğun sunucuda ucu açılarak false-'ölü' azaltılır.
                    PerSampleTimeoutMs = Math.Max(200, probe.IcmpSampleTimeoutMs),
                    SwitchHysteresisMs = Math.Max(0, probe.SwitchHysteresisMs),
                    SlowServerToleranceMs = Math.Max(0, probe.SlowServerToleranceMs),
                    FailoverSwitchCooldownSeconds = Math.Max(0, probe.FailoverSwitchCooldownSeconds),
                    UdpCheck = new UdpHealthCheckOptions(
                        WaitTimeoutMs: Math.Max(200, probe.UdpWaitTimeoutMs)),
                    HandshakeProbe = new WireGuardHandshakeProbeOptions(
                        WaitTimeoutMs: Math.Max(300, probe.HandshakeWaitTimeoutMs),
                        MaxAttempts: 2),
                },
                preferred: preferred);

            Logging.SaveLog($"[GPN] Bağlan sonucu: {snapshot.State} | {snapshot.ProfileSummary} | mod={snapshot.Mode}");
            switch (snapshot.State)
            {
                case GpnConnectionState.Connected:
                    NoticeManager.Instance.SendMessageEx(ResUI.OperationSuccess);
                    break;
                case GpnConnectionState.Failed:
                    NoticeManager.Instance.SendMessageEx($"GPN bağlantı hatası: {snapshot.Error}");
                    break;
            }
            return true;
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"[GPN] Bağlan hatası: {ex.Message}", ex);
            NoticeManager.Instance.SendMessageEx($"GPN bağlantı hatası: {ex.Message}");
            return true; // GPN dalı denendi; tek-profil fallback'i tetikleme.
        }
    }

    /// <summary>
    /// Aktif GPN tünelini yerinde yeniden başlatır (WARP egress otomatik
    /// kurtarması — WarpAutoRecoverService'ün reconnect kancası). Yalnızca
    /// WireGuardUDP modunda ve bağlıyken çalışır; V2rayTCP fallback'inde false.
    /// </summary>
    public Task<bool> ReconnectGpnTunnelAsync(CancellationToken ct)
        => GetGpnCoordinator().ReconnectCurrentTunnelAsync(ct);

    /// <summary>
    /// Belirgin "GPN Bağlan" butonu girişi: kullanıcı açıkça GPN modunu seçtiyse,
    /// seçili profil bir WireGuard profili olmasa bile (ör. VLESS/SS seçiliyken)
    /// İtalya/Almanya otomatik seçimini zorla tetikler. TUN kapalıysa önce açılır
    /// (WireGuard/TUN akışı gerekli). Hâlihazırda GPN koordinatörü bağlıysa bağlantıyı
    /// keser (toggle), aksi halde seçim + bağlantı + failover izleyiciyi başlatır.
    /// </summary>
    public async Task GpnConnectAsync()
    {
        var coordinator = GetGpnCoordinator();
        var active = coordinator.Snapshot.State is GpnConnectionState.Connected or GpnConnectionState.Connecting;
        if (active)
        {
            await coordinator.DisconnectAsync(CancellationToken.None);
            Logging.SaveLog("[GPN] GPN Bağlan butonu ile bağlantı kesildi.");
            return;
        }

        // WireGuard/TUN akışı TUN gerektirir; kullanıcı GPN modunu açıkça seçtiği
        // için gerekirse TUN'u etkinleştir ve kalıcılaştır.
        if (!_config.TunModeItem.EnableTun)
        {
            _config.TunModeItem.EnableTun = true;
            await ConfigHandler.SaveConfig(_config);
            Logging.SaveLog("[GPN] GPN Bağlan: TUN etkinleştirildi.");
        }

        // Kullanıcının GPN seçimi kalıcı ve yetkili: bağlantıdan önce mod Manuel'e
        // yazılır (config + VM, auto-apply tetiklenmeden). Host durum yayınları
        // ('gpn') ve dashboard pill'leri böylece kullanıcı tercihiyle çelişmez;
        // sonraki Reload'lar da aynı seçimle GPN akışını sürdürür.
        if (_config.ConnectionItem is null)
        {
            _config.ConnectionItem = new();
        }
        if (_config.ConnectionItem.Mode != SplitTunnelViewModel.ModeManual)
        {
            _config.ConnectionItem.Mode = SplitTunnelViewModel.ModeManual;
            await ConfigHandler.SaveConfig(_config);
        }
        ConnectionViewModel.SetModeSilently(SplitTunnelViewModel.ModeManual);

        await TryRunGpnConnectAsync();
    }

    public async Task Reload()
    {
        //If there are unfinished reload job, marked with next job.
        if (!await _reloadSemaphore.WaitAsync(0))
        {
            _hasNextReloadJob = true;
            return;
        }

        if (DesignMode)
        {
            _reloadSemaphore.Release();
            return;
        }

        try
        {
            SetReloadEnabled(false);

            // Bağlantı zaten ayakta mıydı? Aşağıdaki otomatik ölçüm yalnızca GERÇEKTEN
            // yeni bir bağlantı kurulduğunda çalışmalı: idempotent bağlanma sayesinde
            // Reload artık tüneli yıkmıyor, ama ölçümü her Reload'da tekrarlamak yine
            // gereksiz sunucu ping'i ve arayüz trafiği üretirdi.
            var coreWasReadyBeforeReload = ReadMainCoreHealth()?.State == CoreHealthState.Ready;

            var profileItem = await ConfigHandler.GetDefaultServer(_config);
            if (profileItem == null)
            {
                NoticeManager.Instance.Enqueue(ResUI.CheckServerSettings);
                return;
            }

            // GPN akışı: kullanıcının GPN/VPN SEÇİMİ yetkilidir — mod Manuel (GPN)
            // ise ve TUN açıksa WireGuard adaylarını (İtalya/Almanya) ölçüp en düşük
            // gecikmeli sunucuyu otomatik seç ve ona bağlan (failover izleyici dahil).
            // Seçili profilin türü bu kararı EZEMEZ: aksi halde WireGuard profili
            // seçiliyken VPN seçimi sessizce GPN tüneline dönüşür (kullanıcı seçimi
            // yok sayılır — "VPN bağlanamıyor"un kaynağı) ve GPN seçiliyken WireGuard
            // dışı bir profil normal VPN akışını çalıştırırdı.
            var userMode = _config.ConnectionItem?.Mode ?? SplitTunnelViewModel.ModeOff;
            var gpnActive = ShouldRunGpnFlow(userMode, _config.TunModeItem.EnableTun);
            var gpnConnected = gpnActive && await TryRunGpnConnectAsync();

            // GPN artık uygulanmıyorsa (kullanıcı VPN moduna geçti, mod Off oldu veya
            // TUN kapandı) aktif bir GPN koordinatörü varsa teardown et: failover
            // izleyicisini iptal et ve GpnCoreLauncher'ın başlattığı tüneli durdur.
            // Aksi halde eski failover döngüsü arka planda yaşar ve normal (tek-profil)
            // akışla çakışır.
            await StopGpnCoordinatorWhenNotConnectedAsync(gpnConnected, _gpnCoordinator);

            if (!gpnConnected)
            {
                var allResult = await CoreConfigContextBuilder.BuildAll(_config, profileItem);
                if (NoticeManager.Instance.NotifyValidatorResult(allResult.CombinedValidatorResult) && !allResult.Success)
                {
                    return;
                }

                try
                {
                    await Task.Run(async () =>
                    {
                        // CoreManager owns the single connect-side proxy transition. It
                        // returns only after the core listener has passed readiness checks.
                        await LoadCore(allResult.MainResult.Context, allResult.PreSocksResult?.Context);
                        await Task.Delay(1000);
                    });
                }
                catch (Exception ex)
                {
                    // CoreEngineHost already surfaced the failure to the user through
                    // UpdateHandler (e.g. "Core executable missing"). Swallow it here so
                    // a missing/broken core cannot escape into the dispatcher as an
                    // unhandled-exception storm on every startup reload.
                    Logging.SaveLog("LoadCore failed", ex);
                }
            }
            // Otomatik sunucu kullanılabilirlik ölçümünü (ping/hız testi) BAĞLANTI
            // KURULDUKTAN SONRAYA ertele: Reload akışının hemen sonunda çalıştırmak,
            // "Bağlanıyor..." durumunu "Hız testi yapılıyor..." ile eziyor ve bağlantı
            // kurulurken kullanıcıya yanlış "kopuyor / test yapıyor" izlenimi veriyor
            // (canlı gözlenen "bağlan → kes → yeniden bağlan" algısının kaynaklarından
            // biri). Çekirdek Ready (bağlantı kuruldu) olunca ölçüm yapılır; bağlantı
            // kurulamazsa (hata / kapatıldı / zaman aşımı) otomatik ölçüm atlanır —
            // manuel ⚡ Test butonu her zaman çalışır.
            if (!coreWasReadyBeforeReload)
            {
                _ = RunAvailabilityCheckAfterConnectAsync();
            }
            else
            {
                Logging.SaveLog("[Reload] Bağlantı zaten kuruluydu — otomatik sunucu ölçümü atlandı.");
            }

            var showClashUI = AppManager.Instance.IsRunningCore(ECoreType.mihomo);
            if (showClashUI)
            {
                //await Observable.Start(async () =>
                //{
                //    await ClashProxiesViewModel.ProxiesReload();
                //}, RxSchedulers.MainThreadScheduler);
                RxSchedulers.MainThreadScheduler.Schedule(async () =>
                {
                    await ClashProxiesViewModel.ProxiesReload();
                });
            }

            ReloadResult(showClashUI);
        }
        finally
        {
            SetReloadEnabled(true);
            _reloadSemaphore.Release();
            //If there is a next reload job, execute it.
            if (_hasNextReloadJob)
            {
                _hasNextReloadJob = false;
                await Reload();
            }
        }
    }

    /// <summary>Otomatik sunucu ölçümü için bağlantı kurma bekleme sınırı.</summary>
    private static readonly TimeSpan AvailabilityCheckConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Çekirdek Ready olduktan sonra ölçümden önceki yerleşme payı.</summary>
    private static readonly TimeSpan AvailabilityCheckSettleDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Bağlantı kurulduktan sonra otomatik sunucu kullanılabilirlik ölçümünü çalıştırır
    /// (<see cref="StatusBarViewModel.TestServerAvailability"/>). Çekirdek Ready durumuna
    /// (bağlantı kuruldu) geçene kadar bekler; kurulamazsa (Failed/Stopped/zaman aşımı)
    /// ölçümü atlar. Ayrılan (fire-and-forget) görevdir — Reload akışını ve bağlantı
    /// durumu gösterimini asla engellemez.
    /// </summary>
    private async Task RunAvailabilityCheckAfterConnectAsync()
    {
        try
        {
            if (!await WaitForCoreReadyAsync(AvailabilityCheckConnectTimeout, CancellationToken.None))
            {
                Logging.SaveLog("[Reload] Bağlantı kurulamadı — otomatik sunucu ölçümü atlandı (manuel ⚡ Test butonu çalışır).");
                return;
            }

            // Bağlantı durumu gösterimi (BAĞLANDI + IP doğrulama) yerleşsin diye kısa
            // bekleme; ardından ölçüm durum satırına (RunningInfoDisplay) yazılır.
            await Task.Delay(AvailabilityCheckSettleDelay);

            RxSchedulers.MainThreadScheduler.Schedule(async () =>
            {
                await StatusBarViewModel.TestServerAvailability();
            });
        }
        catch (Exception ex)
        {
            Logging.SaveLog("[Reload] Otomatik sunucu ölçümü hatası", ex);
        }
    }

    /// <summary>Ana çekirdeğin sağlık durumunu AppManager üzerinden okur.</summary>
    private static CoreHealthSnapshot? ReadMainCoreHealth()
        => AppManager.Instance.CoreEngineHost?.GetHealth(CoreHealthRole.Main);

    /// <summary>
    /// Ana çekirdek Ready durumuna (bağlantı kuruldu) gelene kadar bekler.
    /// Failed/Stopped → false (ölçüm atlanır); çekirdek yoksa → false; zaman aşımı → false.
    /// </summary>
    private static Task<bool> WaitForCoreReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => WaitForCoreReadyAsync(ReadMainCoreHealth, timeout, cancellationToken);

    /// <summary>
    /// <see cref="WaitForCoreReadyAsync(TimeSpan, CancellationToken)"/>'in test edilebilir
    /// hali — sağlık durumu sağlayıcısı dışarıdan verilir (testler sahte durumlar üretir,
    /// AppManager/çekirdek başlatmaz).
    /// </summary>
    internal static async Task<bool> WaitForCoreReadyAsync(
        Func<CoreHealthSnapshot?> healthProvider,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var health = healthProvider();
            if (health is null)
            {
                return false; // çekirdek yok — bekleyecek bağlantı yok
            }
            switch (health.State)
            {
                case CoreHealthState.Ready:
                    return true;  // bağlantı kuruldu
                case CoreHealthState.Failed:
                case CoreHealthState.Stopped:
                    return false; // kurulamaz / kapatıldı — ölçümü atla
            }

            await Task.Delay(500, cancellationToken);
        }
        return false;
    }

    private void ReloadResult(bool showClashUI)
    {
        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            ShowClashUI = showClashUI;
            if (TabMainSelectedIndex < 0)
            {
                TabMainSelectedIndex = 4;
            }
        });
    }

    private void SetReloadEnabled(bool enabled)
    {
        RxSchedulers.MainThreadScheduler.Schedule(() => BlReloadEnabled = enabled);
    }

    private async Task LoadCore(CoreConfigContext? mainContext, CoreConfigContext? preContext)
    {
        if (mainContext is null)
        {
            await CoreManager.Instance.LoadCore(mainContext, preContext);
            return;
        }

        AppManager.Instance.CoreEngineHost ??= new CoreEngineHost(_config, UpdateHandler);
        await AppManager.Instance.CoreEngineHost.StartAsync(mainContext, preContext);
    }

    #endregion core job

    #region Presets

    public async Task ApplyRegionalPreset(EPresetType type)
    {
        await ConfigHandler.ApplyRegionalPreset(_config, type);
        await ConfigHandler.InitRouting(_config);
        RxSchedulers.MainThreadScheduler.Schedule(async () =>
        {
            await StatusBarViewModel.RefreshRoutingsMenu();
        });

        await ConfigHandler.SaveConfig(_config);
        await new UpdateService(_config, UpdateTaskHandler).UpdateGeoFileAll();
        await Reload();
    }

    #endregion Presets
}
