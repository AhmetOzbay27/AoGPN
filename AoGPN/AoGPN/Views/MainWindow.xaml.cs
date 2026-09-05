using System.Reactive.Disposables;
using System.Reactive.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using AoGPN.ViewModels;
using ServiceLib.Handler.Fmt;
using ServiceLib.Handler.SysProxy;
using ServiceLib.Helper;
using ServiceLib.Services;
using ServiceLib.Services.CoreConfig;
using ServiceLib.Services.Gpn;
using AoGPN.Base;
using AoGPN.Common;
using AoGPN.Controls;
using AoGPN.Manager;
using H.NotifyIcon;
using AoGPN.Services;

namespace AoGPN.Views;

public partial class MainWindow : IDashboardBridge
{
    private static Config _config;
    private readonly SerialDisposable _layoutBindingsDisposable = new();
    private CheckUpdateView? _checkUpdateView;
    private BackupAndRestoreView? _backupAndRestoreView;
    private ThemeSettingViewModel? _sidebarThemeVm;

    // Route-test results are pushed to the renderer with camelCase keys to match the
    // rest of the host→renderer payloads.
    private static readonly JsonSerializerOptions RouteTestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly CancellationTokenSource _webViewLifetime = new();
    private readonly SemaphoreSlim _connectionToggleGate = new(1, 1);
    private Task? _connectionLifecycleTask;
    private TelemetryDashboardViewModel? _telemetryDashboard;
    private bool _webViewReady;
    private bool _connectionState;
    private bool _connectionStarting;

    // Bağlantı kurulamayınca dashboard hata kartına taşınan başarısızlık bilgisi:
    // ana çekirdeğin son Failed durumu (kullanıcı dostu Error mesajı) + son
    // başlatma teşhisi (teknik ayrıntı / kod) + GPN koordinatörünün son Failed
    // anlık görüntüsü (seçim/launcher hatası — çekirdek hiç başlamamış olabilir).
    private CoreHealthSnapshot? _lastMainCoreFailure;
    private CoreStartupDiagnostic? _lastMainCoreDiagnostic;
    private GpnConnectionSnapshot? _lastGpnFailedSnapshot;


    // WARP egress otomatik kurtarma — WARP dial sağlığı faulted olunca aktif WG
    // tünelini yeniden başlatır (bkz. WarpAutoRecoverService).
    private WarpAutoRecoverService? _warpAutoRecover;
    // WARP faulted iken launcher egress'ini restart'sız DIRECT'e çeker
    // (bkz. GpnBypassEgressController — dashboard rozeti Degraded bayrağını okur).
    private GpnBypassEgressController? _bypassEgressController;

    // Last rule-drift verdict pushed to the dashboard ("InSync"/"Drifted"/...).
    // The periodic health check only republishes when the verdict changes, so the
    // banner never flickers on every 30 s tick.
    private string _activeView = "dashboard";
    private bool _allowClose;
    private bool _isClosing;
    // Set while the AutoHideStartup startup transition is in flight. The startup
    // minimize in the constructor must not race ahead of the tray icon creation
    // (see OnLoaded); while this is true, StateChanged ignores the minimize so
    // the window is not hidden before the tray icon exists.
    private bool _startupTrayPending;

    // Nodes already advised about the sing-box -> Xray REALITY fallback, keyed by
    // "IndexId|tunEnabled" so the advice fires once per node per TUN state but can
    // be re-armed when the user toggles TUN.
    private readonly HashSet<string> _realityFallbackAdvisedNodes = new(StringComparer.Ordinal);

    private readonly DashboardHost _dashboardHost;
    // Runs the local SOCKS/HTTP listener without an active tunnel so the system proxy
    // works while "disconnected", like v2rayN. Reconciles on proxy-mode changes,
    // connection toggles, node switches and startup.
    private readonly SystemProxyOnlyService _proxyOnlyService;

    // Warns before connecting when another VPN stack (v2rayN's TUN or a foreign
    // listener on the local proxy port) would collide with AoGPN's tunnel.
    // Initialized in the constructor because it references _proxyOnlyService.
    private readonly ForeignTunnelDetector _foreignTunnelDetector;
    // Guards the tray-hide invariant: minimizing/close-to-tray only hides the window
    // and never stops the core or clears the OS proxy; only the exit path may.
    private readonly TrayWindowCoordinator _trayBehavior;
    private readonly WindowLifecycleService _windowLifecycle = new();
    private readonly ConnectionCoordinator _connectionCoordinator = new();
    private readonly AoGPN.Services.DashboardPublisher _dashboardPublisher;
    private readonly DashboardSettingsService _settingsService;
    private readonly DashboardGpnServerService _gpnServerService;
    private readonly DashboardNodeService _nodeService;
    private readonly DashboardPushService _pushService;
    private readonly DashboardMessageDispatcher _dashboardMessageDispatcher;

    public MainWindow()
    {
        InitializeComponent();
        _proxyOnlyService = new SystemProxyOnlyService(NotifyNodesOpAsync);
        _foreignTunnelDetector = new ForeignTunnelDetector(
            isAppCoreRunning: () =>
            {
                var health = AppManager.Instance.CoreEngineHost?.GetHealth(CoreHealthRole.Main);
                return health?.State is CoreHealthState.Starting
                    or CoreHealthState.Ready
                    or CoreHealthState.Degraded
                    || _proxyOnlyService.IsRunning;
            });
        _trayBehavior = new TrayWindowCoordinator(
            hideWindow: () => ShowHideWindow(false),
            showTrayHint: ShowTrayMinimizeHint,
            stopCoreAsync: () => AppManager.Instance.StopCoreAsync(),
            clearProxyAsync: () => SysProxyHandler.UpdateSysProxy(_config, true),
            flushAsync: () => AppManager.Instance.FlushStateAsync(),
            shutdown: () =>
            {
                _allowClose = true;
                Application.Current.Shutdown();
            });
        _dashboardHost = new DashboardHost(WebView, AppContext.BaseDirectory);
        _dashboardMessageDispatcher = new DashboardMessageDispatcher(this);
        _settingsService = new DashboardSettingsService(
            executeScript: ExecuteScriptSafelyAsync,
            isWebViewReady: () => _webViewReady,
            readTransport: ReadTransport,
            proxyOnlyService: _proxyOnlyService);
        _gpnServerService = new DashboardGpnServerService(
            executeScript: ExecuteScriptSafelyAsync,
            isWebViewReady: () => _webViewReady,
            getViewModel: () => ViewModel);
        _nodeService = new DashboardNodeService(
            executeScript: ExecuteScriptSafelyAsync,
            isWebViewReady: () => _webViewReady,
            isClosing: () => _isClosing,
            notifyNodesOp: NotifyNodesOpAsync,
            getProfilesViewModel: () => ViewModel?.ProfilesViewModel,
            invokeOnUiThread: action => Dispatcher.InvokeAsync(action),
            proxyOnlyService: _proxyOnlyService,
            pushSystemProxyState: force => PushSystemProxyStateAsync(force),
            updateTrayStatus: UpdateTrayStatus);
        _pushService = new DashboardPushService(
            executeScript: ExecuteScriptSafelyAsync,
            isWebViewReady: () => _webViewReady,
            getConnectionViewModel: () => ViewModel?.ConnectionViewModel,
            readTransport: ReadTransport,
            readActualConnectionState: ReadActualConnectionState,
            getActiveView: () => _activeView,
            getWebViewToken: () => _webViewLifetime.Token,
            getBypassEgressController: () => _bypassEgressController);
        _dashboardPublisher = new AoGPN.Services.DashboardPublisher(ExecuteScriptSafelyAsync);
        _connectionCoordinator.SnapshotChanged += snapshot =>
        {
            if (!_isClosing)
            {
                _ = Dispatcher.InvokeAsync(() => _dashboardPublisher.PublishAsync(snapshot));
            }
        };
        _dashboardHost.WebMessageReceived += _dashboardMessageDispatcher.HandleWebMessageReceived;
        _dashboardHost.NavigationCompleted += CoreWebView2_NavigationCompleted;

        // Loaded is used instead of the constructor so WebView2 is initialized after
        // WPF has created a native window handle for the control.
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;

        _config = AppManager.Instance.Config;

        // Tray quick-connect and connection-mode switching go through the same
        // gated paths as the dashboard buttons (ToggleConnectionAsync /
        // SetDashboardModeAsync), so a tray click can never bypass the connection
        // gate, TUN elevation check or persisted routing rules.
        // MainWindow lives for the whole app, so these subscriptions are never
        // disposed — they keep the tray controls working even while the window is
        // hidden to the notification area.
        StatusBarViewModel.Instance.ToggleConnectionRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async _ => await ToggleConnectionFromTrayAsync());
        StatusBarViewModel.Instance.SetConnectionModeRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async mode => await SetDashboardModeAsync(mode));
        // WPF-side system-proxy changes (status-bar combobox, sidebar combobox,
        // global hotkeys) funnel through the same gated path the dashboard uses
        // (SetSystemProxyModeAsync), which reconciles the proxy-only core so the OS
        // proxy keeps working while "disconnected" (v2rayN behaviour).
        StatusBarViewModel.Instance.SystemProxyModeRequested
            .AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async type => await SetSystemProxyModeAsync(type));

        // GPN direnç kararlarını dashboard'a taşı (sunucu değişimi / UDP ölümü /
        // mod düşüşü / Tier-2 kurtarma). PublishAsync gibi yalnızca JS fonksiyonunu
        // çağırır; GpnServerSelectionService zaten DiagLog'a da yazdığı için burada
        // ek ızgara gerekmez.
        AppEvents.GpnResilienceChanged.AsObservable()
            .Subscribe(async evt => await PushGpnResilienceAsync(evt));

        // Kesintisiz düğüm geçişi sonrası eski düğümün boşalma (drain) ilerlemesini
        // dashboard durum satırına taşı — GpnDrainWatcher /connections zincirlerinden
        // eski wg-<id>'ye bağlı kalan oturum sayısını yayınlar.
        AppEvents.GpnDrainChanged.AsObservable()
            .Subscribe(async snap => await PushGpnDrainAsync(snap));

        // GPN koordinatörü durum değiştirdiğinde (Connecting → Connected) ana bağlantı
        // butonunu eşitle: çekirdek hazır olur olmaz "Bağlantıyı Kes" gösterilir, 2 sn'lik
        // telemetri döngüsü beklenmez. Böylece "bağlandı ama buton hâlâ Bağlan diyor"
        // ve bağlanma sırasında yanlış "kesildi" yanıp sönmesi oluşmaz.
        // WebView2, ViewModel ve StatusBarViewModel yalnızca UI iş parçacığında
        // okunabildiğinden bu akışı ana iş parçacığına taşı; aksi halde koordinatörün
        // arka plan iş parçacığında Subscribe ReadTransport() içinde cross-thread
        // InvalidOperationException fırlatır (ReactiveWindow.ViewModel GetValue).
        AppEvents.GpnConnectionStateChanged.AsObservable()
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(async snapshot => await OnGpnConnectionSnapshotAsync(snapshot));

        // GPN diyagnoz akışı (GPN_LOG / GPN_RECOVER / GPN_SELECT / GPN_FAILOVER /
        // GPN_LAUNCH ...): DiagLog "GPN_*" satırlarını dashboard tanı akışına taşır.
        AppEvents.GpnDiagChanged.AsObservable()
            .Subscribe(async evt => await PushGpnDiagAsync(evt));            AppEvents.GpnCaptureStatsChanged.AsObservable()
            .Subscribe(async evt =>
            {
                _pushService.UpdateLastCaptureStats(evt);
                await _pushService.PushGpnCaptureStatsAsync();
            });

        // Sunucu kullanılabilirlik ölçümü (hız testi) sonucunu dashboard ana paneline
        // taşı: bağlantı kurulduktan sonra ölçülen gecikme + IP + sunucu adı Ping
        // kartında görünür — ölçüm yalnızca WPF durum çubuğunda kalmaz.
        AppEvents.AvailabilityCheckCompleted.AsObservable()
            .Subscribe(async result => await PushAvailabilityInfoAsync(result));

        // Çekirdek başlatma başarısızlıklarını hatırla: Reload akışı bağlantı
        // kuramayınca dashboard hata kartına "neden" yazılır. Kullanıcı dostu mesaj
        // CoreHealthSnapshot.Error içindedir; teknik ayrıntı/port/elevation bilgisi
        // CoreStartupDiagnostic'te. Ready/Stopped'a geçince temizlenir (eski hata
        // kartı yeni denemede gösterilmez).
        AppEvents.CoreHealthChanged.AsObservable()
            .Subscribe(health =>
            {
                if (health.Role != CoreHealthRole.Main)
                {
                    return;
                }
                if (health.State == CoreHealthState.Failed)
                {
                    _lastMainCoreFailure = health;
                }
                else if (health.State is CoreHealthState.Ready or CoreHealthState.Stopped)
                {
                    _lastMainCoreFailure = null;
                }
            });
        AppEvents.CoreStartupDiagnosticChanged.AsObservable()
            .Subscribe(diag =>
            {
                if (diag.Role == CoreHealthRole.Main)
                {
                    _lastMainCoreDiagnostic = diag;
                }
            });

        ThreadPool.RegisterWaitForSingleObject(App.ProgramStarted, OnProgramStarted, null, -1, false);

        App.Current.SessionEnding += Current_SessionEnding;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        menuSettingsSetUWP.Click += MenuSettingsSetUWP_Click;
        menuPromotion.Click += MenuPromotion_Click;
        menuClose.Click += MenuClose_Click;
        menuCheckUpdate.Click += MenuCheckUpdate_Click;
        menuOpenLogFolder.Click += menuOpenLogFolder_Click;
        menuVerboseLogging.Click += menuVerboseLogging_Click;
        btnNewUpdate.Click += MenuCheckUpdate_Click;
        menuBackupAndRestore.Click += MenuBackupAndRestore_Click;

        // Restore verbose logging check state from config
        menuVerboseLogging.IsChecked = _config.GuiItem.EnableVerboseLog;
        btnNavServers.Click += (_, _) => { SetActiveNav(btnNavServers); tabMain2.SelectedIndex = 0; };
        btnNavMsg.Click += (_, _) => { SetActiveNav(btnNavMsg); tabMain2.SelectedIndex = 1; };
        btnNavAddServer.Click += (_, _) =>
        {
            SetActiveNav(btnNavAddServer);
            btnNavAddServer.ContextMenu!.IsOpen = true;
        };
        btnNavImport.Click += (_, _) => SetActiveNav(btnNavImport);
        btnNavScan.Click += (_, _) => SetActiveNav(btnNavScan);
        btnNavConnection.Click += (_, _) => { SetActiveNav(btnNavConnection); tabMain2.SelectedIndex = 4; };
        btnNavGameBoost.Click += (_, _) => { SetActiveNav(btnNavGameBoost); tabMain2.SelectedIndex = 4; };
        btnNavProxies.Click += (_, _) => { SetActiveNav(btnNavProxies); tabMain2.SelectedIndex = 2; };
        btnNavConnections.Click += (_, _) => { SetActiveNav(btnNavConnections); tabMain2.SelectedIndex = 3; };
        btnNavSettings.Click += (_, _) => SetActiveNav(btnNavSettings);
        btnNavRouting.Click += (_, _) => SetActiveNav(btnNavRouting);
        btnNavDNS.Click += (_, _) => SetActiveNav(btnNavDNS);
        tabMain2.SelectionChanged += (_, _) => SyncActiveNav(tabMain2.SelectedIndex);

        // Populate the sidebar theme dropdown and react to changes.
        cmbSidebarTheme.ItemsSource = Utils.GetEnumNames<ETheme>();
        cmbSidebarTheme.SelectedValue = _config.UiItem.CurrentTheme ?? nameof(ETheme.Dark);
        _sidebarThemeVm = new ThemeSettingViewModel();
        cmbSidebarTheme.SelectionChanged += (_, _) =>
        {
            if (cmbSidebarTheme.SelectedValue is string theme &&
                theme != _config.UiItem.CurrentTheme)
            {
                _config.UiItem.CurrentTheme = theme;
                _ = ConfigHandler.SaveConfig(_config);
                _sidebarThemeVm.CurrentTheme = theme;
                _sidebarThemeVm.ModifyTheme();
            }
        };

        if (_config.UiItem.SidebarWidth > 0)
        {
            colSidebar.Width = new GridLength(_config.UiItem.SidebarWidth);
        }
        SetActiveNav(btnNavConnection);

        this.WhenActivated(disposables =>
        {
            //servers
            this.BindCommand(ViewModel, vm => vm.AddVmessServerCmd, v => v.menuAddVmessServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddVlessServerCmd, v => v.menuAddVlessServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddShadowsocksServerCmd, v => v.menuAddShadowsocksServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddSocksServerCmd, v => v.menuAddSocksServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddHttpServerCmd, v => v.menuAddHttpServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddTrojanServerCmd, v => v.menuAddTrojanServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddHysteria2ServerCmd, v => v.menuAddHysteria2Server).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddTuicServerCmd, v => v.menuAddTuicServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddWireguardServerCmd, v => v.menuAddWireguardServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddAnytlsServerCmd, v => v.menuAddAnytlsServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddNaiveServerCmd, v => v.menuAddNaiveServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddCustomServerCmd, v => v.menuAddCustomServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddPolicyGroupServerCmd, v => v.menuAddPolicyGroupServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddProxyChainServerCmd, v => v.menuAddProxyChainServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddServerViaClipboardCmd, v => v.menuAddServerViaClipboard).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddServerViaClipboardCmd, v => v.btnNavImport).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddServerViaScanCmd, v => v.menuAddServerViaScan).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddServerViaScanCmd, v => v.btnNavScan).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddServerViaImageCmd, v => v.menuAddServerViaImage).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddCustomServerCmd, v => v.navAddCustomServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddVmessServerCmd, v => v.navAddVmessServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddVlessServerCmd, v => v.navAddVlessServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddShadowsocksServerCmd, v => v.navAddShadowsocksServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddTrojanServerCmd, v => v.navAddTrojanServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddHysteria2ServerCmd, v => v.navAddHysteria2Server).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddWireguardServerCmd, v => v.navAddWireguardServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddSocksServerCmd, v => v.navAddSocksServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddHttpServerCmd, v => v.navAddHttpServer).DisposeWith(disposables);

            //sub
            this.BindCommand(ViewModel, vm => vm.SubSettingCmd, v => v.menuSubSetting).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SubUpdateCmd, v => v.menuSubUpdate).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SubUpdateViaProxyCmd, v => v.menuSubUpdateViaProxy).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SubGroupUpdateCmd, v => v.menuSubGroupUpdate).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SubGroupUpdateViaProxyCmd, v => v.menuSubGroupUpdateViaProxy).DisposeWith(disposables);

            //setting
            this.BindCommand(ViewModel, vm => vm.OptionSettingCmd, v => v.menuOptionSetting).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.OptionSettingCmd, v => v.btnNavSettings).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RoutingSettingCmd, v => v.menuRoutingSetting).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RoutingSettingCmd, v => v.btnNavRouting).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.DNSSettingCmd, v => v.btnNavDNS).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.DNSSettingCmd, v => v.menuDNSSetting).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.FullConfigTemplateCmd, v => v.menuFullConfigTemplate).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.GlobalHotkeySettingCmd, v => v.menuGlobalHotkeySetting).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RebootAsAdminCmd, v => v.menuRebootAsAdmin).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ClearServerStatisticsCmd, v => v.menuClearServerStatistics).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.OpenTheFileLocationCmd, v => v.menuOpenTheFileLocation).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RegionalPresetDefaultCmd, v => v.menuRegionalPresetsDefault).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RegionalPresetRussiaCmd, v => v.menuRegionalPresetsRussia).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RegionalPresetIranCmd, v => v.menuRegionalPresetsIran).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.ReloadCmd, v => v.menuReload).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.BlReloadEnabled, v => v.menuReload.IsEnabled).DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.BlNewUpdate, v => v.btnNewUpdate.Visibility).DisposeWith(disposables);

            _layoutBindingsDisposable.DisposeWith(disposables);

            // AoGPN uses a single-pane layout; servers/proxies/logs live in the left rail tabs.
            InitLayoutBindings();

            this.WhenAnyValue(v => v.ViewModel.StatusBarViewModel)
                .Subscribe(vm =>
                {
                    ViewHost.Show(contentStatusBarView, vm);
                    BindSidebarConnectionMode(vm);
                })
                .DisposeWith(disposables);

            ViewModel.ReadTextFromClipboardInteraction.RegisterHandler(interaction =>
            {
                var clipboardData = WindowsUtils.GetClipboardData();
                interaction.SetOutput(clipboardData);
            }).DisposeWith(disposables);

            ViewModel.ScanScreenInteraction.RegisterHandler(interaction =>
            {
                ShowHideWindow(false);
                if (Application.Current?.MainWindow is { } window)
                {
                    var bytes = QRCodeWindowsUtils.CaptureScreen(window);
                    interaction.SetOutput(bytes);
                }
                ShowHideWindow(true);
            }).DisposeWith(disposables);

            ViewModel.BrowseImageFileInteraction.RegisterHandler(interaction =>
            {
                if (UI.OpenFileDialog(out var fileName, "PNG|*.png|All|*.*") != true)
                {
                    interaction.SetOutput(null);
                    return;
                }
                interaction.SetOutput(fileName);
            }).DisposeWith(disposables);

            ViewModel.ShowHideWindowInteraction.RegisterHandler(interaction =>
            {
                ShowHideWindow(interaction.Input);
                interaction.SetOutput(Unit.Default);
            }).DisposeWith(disposables);

            AppEvents.SendSnackMsgRequested
              .AsObservable()
              .ObserveOn(RxSchedulers.MainThreadScheduler)
              .Subscribe(async content => await DelegateSnackMsg(content))
              .DisposeWith(disposables);

            AppEvents.SendSnackActionRequested
              .AsObservable()
              .ObserveOn(RxSchedulers.MainThreadScheduler)
              .Subscribe(async notice => await DelegateSnackAction(notice))
              .DisposeWith(disposables);

            AppEvents.AppExitRequested
              .AsObservable()
              .ObserveOn(RxSchedulers.MainThreadScheduler)
              .Subscribe(_ => StorageUI())
              .DisposeWith(disposables);

            AppEvents.ShutdownRequested
             .AsObservable()
             .ObserveOn(RxSchedulers.MainThreadScheduler)
             .Subscribe(Shutdown)
             .DisposeWith(disposables);

            // Push WPF theme changes to the WebView2 gaming dashboard.
            AppEvents.ThemeChanged
             .AsObservable()
             .ObserveOn(RxSchedulers.MainThreadScheduler)
             .Subscribe(async id => await PushThemeAsync(id))
             .DisposeWith(disposables);

            // Push language changes from the native settings window to the WebView
            // dashboard so they apply instantly, just like theme changes.
            AppEvents.LanguageChanged
             .AsObservable()
             .ObserveOn(RxSchedulers.MainThreadScheduler)
             .Subscribe(async _ => await PushLanguageAsync())
             .DisposeWith(disposables);

            // WARP dial sağlık monitörü: sing-box log kuyruğunu izler, warp
            // outbound hatalarını (no route to host vb.) sayar ve faulted olunca
            // hem snackbar hem dashboard banner'ı besler (bkz. WarpDialHealthMonitor).
            var warpHealth = WarpDialHealthMonitor.Instance;
            warpHealth.Start();
            AppEvents.WarpDialHealthChanged
             .AsObservable()
             .ObserveOn(RxSchedulers.MainThreadScheduler)
             .Subscribe(_ => PushWarpHealthAsync())
             .DisposeWith(disposables);

            // WARP egress otomatik kurtarma: WARP SOCKS5 sunucuya yalnızca WG tüneli
            // üzerinden erişilir; dial hataları faulted'e ulaşınca aktif tünel yeniden
            // başlatılır (cooldown + oturum başına deneme limiti — bkz. WarpAutoRecoverService).
            // Kullanıcı ayarı GuiItem.GpnEnableWarpAutoRecover (varsayılan true).
            _warpAutoRecover = new WarpAutoRecoverService(
                ct => ViewModel.ReconnectGpnTunnelAsync(ct),
                isEnabled: () => _config.GuiItem.GpnEnableWarpAutoRecover);
            _warpAutoRecover.Start();

            // WARP degrade-egress: faulted iken BSG launcher/API satırları + "warp"
            // rotalı girişler mihomo superset oturumunda canlı (restart'sız) DIRECT'e
            // çekilir — WARP ölüyken launcher auth hata yerine doğrudan çıkar;
            // sağlık gelince warp egress üyesine geri döner (bkz. GpnBypassEgressController).
            _bypassEgressController = new GpnBypassEgressController();
            _bypassEgressController.Start();

            // WinDivert yakalama köprüsü sağlığı: DLL/sürücü dosyalarını ve sürücüyü
            // kontrol eder, gerekiyorsa SCM ile kurar. Köprü flame'inin bilinen
            // nedeni exe yanında WinDivert.dll olmamasıydı — durum artık açılışta
            // diag'e + dashboard banner'ına düşer (bkz. WinDivertHealthMonitor).
            var winDivertHealth = WinDivertHealthMonitor.Instance;
            winDivertHealth.Check(attemptInstall: true);
            AppEvents.WinDivertHealthChanged
             .AsObservable()
             .ObserveOn(RxSchedulers.MainThreadScheduler)
             .Subscribe(_ => PushWinDivertHealthAsync())
             .DisposeWith(disposables);
        });

        Title = $"{Utils.GetVersion()} - {(Utils.IsAdministrator() ? ResUI.RunAsAdmin : ResUI.NotRunAsAdmin)}";
        if (_config.UiItem.AutoHideStartup)
        {
            // Start minimized without a visible flash. The actual tray transition is
            // deferred to OnLoaded so the tray icon exists first; until then the
            // startup minimize must not trigger the minimize-to-tray path.
            WindowState = WindowState.Minimized;
            _startupTrayPending = true;
        }

        if (!_config.GuiItem.EnableHWA)
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        AddHelpMenuItem();
        WindowsManager.Instance.RegisterGlobalHotkey(_config, OnHotkeyHandler, null);
    }

    #region WebView2 bridge

    /// <summary>
    /// Restores the native thick frame (hidden for the borderless look) so the
    /// window keeps real resize borders even though WebView2 covers the whole
    /// client area, and clamps maximized bounds to the work area so the window
    /// never slides under the taskbar. A plain WS_POPUP window has neither;
    /// WindowChrome's synthetic borders were unusable because the WebView2 child
    /// HWND intercepts the client-area hit testing they rely on.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlStyle);
        style |= WsThickFrame | WsMaximizeBox;
        SetWindowLong(handle, GwlStyle, style);

        MeasureNonClientBorders(handle);

        HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
    }

    private int _borderLeft;
    private int _borderTop;
    private int _borderRight;
    private int _borderBottom;

    /// <summary>
    /// The invisible thick-frame border insets the client area from the window
    /// edges. Measuring it once lets the maximize clamp extend the window by
    /// exactly this amount, so the rendered client fills the work area with no
    /// visible desktop gap around it.
    /// </summary>
    private void MeasureNonClientBorders(IntPtr handle)
    {
        GetWindowRect(handle, out var windowRect);
        GetClientRect(handle, out var clientRect);
        var clientOrigin = new NativePoint { X = 0, Y = 0 };
        ClientToScreen(handle, ref clientOrigin);

        _borderLeft = clientOrigin.X - windowRect.Left;
        _borderTop = clientOrigin.Y - windowRect.Top;
        _borderRight = windowRect.Right - (clientOrigin.X + clientRect.Right);
        _borderBottom = windowRect.Bottom - (clientOrigin.Y + clientRect.Bottom);
    }

    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonUp = 0x0202;
    private const int WmCaptureChanged = 0x0215;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const int MonitorDefaultToNearest = 2;

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_manualDrag)
        {
            if (msg == WmMouseMove)
            {
                OnManualDragMove();
                handled = true;
                return IntPtr.Zero;
            }

            if (msg == WmLButtonUp)
            {
                ReleaseCapture();
                _manualDrag = false;
                handled = true;
                return IntPtr.Zero;
            }

            // Capture stolen externally (Alt+Tab, system menu, another window):
            // abandon the manual drag so the window does not keep following the
            // cursor after the button is released elsewhere.
            if (msg == WmCaptureChanged)
            {
                _manualDrag = false;
                handled = true;
                return IntPtr.Zero;
            }
        }

        if (msg != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        // WPF's borderless maximize sizes the window to the whole monitor; clamp
        // it to the work area of the monitor the window is on (multi-monitor safe)
        // so the taskbar stays visible and no content is cut off underneath it.
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            // Extend the maximized window beyond the work area by the measured
            // non-client border so the invisible border sits off-screen and the
            // client (WebView2) fills the work area edge to edge. The extra +1 on
            // right/bottom compensates the DWM growing the border by one pixel on
            // maximize; it biases toward an invisible 1 px overhang rather than a
            // visible desktop gap.
            mmi.ptMaxPosition.X = info.rcWork.Left - _borderLeft;
            mmi.ptMaxPosition.Y = info.rcWork.Top - _borderTop;
            mmi.ptMaxSize.X = (info.rcWork.Right - info.rcWork.Left) + _borderLeft + _borderRight + 1;
            mmi.ptMaxSize.Y = (info.rcWork.Bottom - info.rcWork.Top) + _borderTop + _borderBottom + 1;
            Marshal.StructureToPtr(mmi, lParam, false);
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Creates the WebView2 environment, applies safe browser settings, and serves the
    /// packaged dashboard through a fixed virtual host instead of an arbitrary file URL.
    /// </summary>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;

        // First run has no saved window size; open the dashboard maximized so it
        // fills the work area instead of the default small frame. Later sessions
        // restore exactly what the user left (see WindowBase.OnLoaded).
        if (ConfigHandler.GetWindowSizeItem(AppManager.Instance.Config, GetType().Name) is null)
        {
            WindowState = WindowState.Maximized;
        }
        else if (Width < MinWidth || Height < MinHeight)
        {
            // Guard against a corrupt saved size (older builds could persist a
            // degenerate 157x25 entry): fall back to the designed default instead
            // of opening a sliver of a window.
            Width = 1200;
            Height = 800;
        }

        try
        {
            await InitializeWebViewAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("WebView2 initialization failed", ex);
            MessageBox.Show(
                this,
                "AO GPN could not initialize Microsoft WebView2. Install the Evergreen WebView2 Runtime and restart the application.",
                "AO GPN",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task InitializeWebViewAsync()
    {
        if (_isClosing || _dashboardHost.IsReady)
        {
            return;
        }

        await _dashboardHost.InitializeAsync(_webViewLifetime.Token);
    }

    /// <summary>
    /// Sends the initial state after the local document has finished loading. Starting
    /// the timer here prevents scripts from being evaluated before the page exists.
    /// </summary>
    private async void CoreWebView2_NavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        if (!e.IsSuccess)
        {
            Logging.SaveLog($"AoGPN dashboard navigation failed: {e.WebErrorStatus}");
            return;
        }

        try
        {
            _webViewReady = true;
            // If AutoHideStartup left the window minimized, freeze the WebView2
            // right away so the hidden dashboard never starts compositing.
            await SyncWebViewSuspensionAsync(WindowState == WindowState.Minimized);
            await SynchronizeConnectionStateAsync(forcePublish: true);
            await SynchronizeWindowStateAsync();
            await PushNodeListAsync();
            await PushSettingsAsync();
            await PushMonitorSnapshotAsync(force: true);
            await PushGpnTelemetryAsync();
            await PushGpnCaptureStatsAsync();

            // Push the startup WinDivert/environment state (bridge availability)
            // and the current warp dial health so banners render immediately
            // instead of waiting for the next event.
            await PushWinDivertHealthAsync();
            await PushWarpHealthAsync();


            // Sync initial theme so the dashboard loads with the correct palette.
            var wpfTheme = AppManager.Instance.Config.UiItem.CurrentTheme ?? nameof(ETheme.Dark);
            var webThemeId = wpfTheme switch
            {
                nameof(ETheme.Dusk) => "plasma",
                nameof(ETheme.NightSky) => "cryo",
                nameof(ETheme.Aquatic) => "matrix",
                nameof(ETheme.Desert) => "inferno",
                nameof(ETheme.Light) => "phantom",
                nameof(ETheme.Crimson) => "crimson",
                nameof(ETheme.Velocity) => "velocity",
                nameof(ETheme.Venom) => "venom",
                nameof(ETheme.Synthwave) => "synthwave",
                nameof(ETheme.Cyberpunk) => "cyberpunk",
                _ => "nebula",
            };
            await PushThemeAsync(webThemeId);
            await PushEffectsTierAsync();
            await PushLanguageAsync();
            await PushAppInfoAsync();
            await ShowHwaFallbackNoticeIfNeededAsync();

            // Auto-run (start on boot) may fire before the network stack is ready.
            // Wait for network availability first so the proxy-only core and OS proxy
            // come up on a live connection instead of pointing at an unreachable node.
            // Manual launches never block on this.
            if (_config.GuiItem.AutoRun)
            {
                await Utils.WaitForNetworkAsync();
            }

            // Restore the independent system-proxy state on launch: if the saved
            // preference is Set/PAC and no connection is active, start the proxy-only
            // core so the OS proxy works immediately (v2rayN behaviour).
            try
            {
                await _proxyOnlyService.ReconcileAsync(AppManager.Instance.Config);
                await PushSystemProxyStateAsync(force: true);
            }
            catch (Exception ex)
            {
                Logging.SaveLog("AoGPN proxy-only startup reconcile failed", ex);
            }

            UpdateTrayStatus();

            // Prime the ISP IP cache early so tunnel verification works from the first connect.
            _ = Task.Run(CacheIspIpAsync);

            StartTelemetryLoop();
            StartTelemetryBridge();

            // Gerçek ping "önce" ölçümü: program açılışında GPN listesindeki
            // uygulamaların kayıtlı sunucu uç noktalarına doğrudan yoldan (VPN'siz)
            // ping atılır — GPN bağlantısı kurulunca "sonra" ölçümüyle karşılaştırılır.
            // Ağ/DB ısınması için kısa bir gecikmeyle arka planda koşar (best-effort).
            _ = MeasureRealPingAsync(isBefore: true, delay: TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            // Event handlers are async-void; contain unexpected browser teardown
            // errors so they cannot escape onto WPF's dispatcher as fatal exceptions.
            if (!_isClosing)
            {
                Logging.SaveLog("AoGPN dashboard startup script failed", ex);
            }
        }
    }

    /// <summary>
    /// Applies the same mode transition used by the native ConnectionView. The command
    /// is serialized so repeated clicks cannot overlap rule writes or core reloads.
    /// </summary>
    public async Task ToggleConnectionAsync(string requestedMode, string transport)
    {
        if (!await _connectionToggleGate.WaitAsync(0))
        {
            return;
        }

        // Reject any connect/disconnect while the real exit path is running.
        // Otherwise a connect clicked during the (multi-second) exit teardown
        // launches a brand-new core that the exit immediately tears down again
        // — observed live: exit began, user clicked connect, a fresh sing-box
        // was launched and then killed, and the user saw "connected but no
        // connectivity" with a ~2 minute unresponsive window.
        if (_trayBehavior.IsExitInProgress)
        {
            Logging.SaveLog("ToggleConnection ignored: application exit already in progress.");
            return;
        }

        try
        {
            var connectionViewModel = ViewModel?.ConnectionViewModel;
            if (connectionViewModel is null)
            {
                return;
            }

            if (ReadActualConnectionState())
            {
                _connectionStarting = false;
                // Disconnect: release capture and clear any transport override so
                // ModeOff maps to the clean (no TUN, no system proxy) default.
                connectionViewModel.Transport = "";
                await ApplyConnectionModeAsync(connectionViewModel, SplitTunnelViewModel.ModeOff);

                // VPN oturum günlüğünü kapat (aktif değilse no-op — GPN bağlantı
                // kesmesi VPN bloğunu etkilemez).
                VpnSessionLog.EndSession("VPN disconnect");
                DiagLog.Write("VPN_LOG disconnect");
            }
            else
            {
                _connectionStarting = true;
                await SendConnectionStateAsync();
                // Global VPN mode always uses TUN capture to route all traffic.
                // GPN (Game Tunnel) mode respects the requested transport.
                var effectiveTransport = requestedMode == "vpn" ? "tun" : transport;
                connectionViewModel.Transport = effectiveTransport;
                var targetMode = requestedMode == "gpn"
                    ? SplitTunnelViewModel.ModeManual
                    : SplitTunnelViewModel.ModeVpn;

                // VPN (normal) akışı kendi oturum günlüğünü açar — vpn-session.log;
                // GPN akışı GpnConnectionCoordinator'da gpn-session.log açar.
                if (requestedMode == "vpn")
                {
                    VpnSessionLog.BeginSession("VPN connect");
                    DiagLog.Write($"VPN_LOG connect start transport={effectiveTransport}");
                }

                // The protocol strategy is a real preference: if the active node
                // cannot speak the chosen protocol, switch to the best matching node
                // (or surface a notice when none exists) before starting the tunnel.
                var protocolNotice = await EnsureProtocolCompatibleAsync();
                await ApplyConnectionModeAsync(connectionViewModel, targetMode);
                // Hold the "connecting" state until the core actually answers the
                // local listener (leaves Starting). Publishing each transient
                // not-yet-Ready read as "disconnected" is what made the dashboard
                // flash connected -> canceled -> connected during startup.
                await WaitForCoreLeavingStartingAsync();
                _connectionStarting = false;
                await SynchronizeConnectionStateAsync(forcePublish: true);
                // Bağlantı kurulamadıysa (çekirdek Failed / GPN koordinatörü Failed)
                // nedeni gösteren hata kartını dashboard'a bas.
                await TryPushConnectionFailureAsync();
                if (requestedMode == "vpn")
                {
                    DiagLog.Write("VPN_LOG connected transport=" + effectiveTransport);
                }
                if (protocolNotice.IsNotEmpty())
                {
                    NoticeManager.Instance.Enqueue(protocolNotice);
                }
            }
        }
        catch (Exception ex)
        {
            // Bağlantı sırasında hata — VPN oturumuna başarısızlığı yaz, akışı düşür.
            DiagLog.Write($"VPN_LOG failed: {ex.Message}");
            throw;
        }
        finally
        {
            _connectionStarting = false;
            _connectionToggleGate.Release();
        }
    }

    /// <summary>
    /// Belirgin "GPN Bağlan" butonu: GPN modu açıkça seçildiğinde seçili profil
    /// WireGuard olmasa bile İtalya/Almanya otomatik seçimini zorla tetikler
    /// (ToggleConnectionAsync'ın seçili-profile bağımlı dalı yerine doğrudan
    /// ViewModel koordinatörünü kullanır). Bağlantı kesme, çekirdek geçişini
    /// temizler.
    /// </summary>
    public async Task RunGpnConnectAsync()
    {
        if (!await _connectionToggleGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var viewModel = ViewModel;
            if (viewModel is null)
            {
                return;
            }

            if (viewModel.GpnConnectCmd is not null)
            {
                _connectionStarting = true;
                await SendConnectionStateAsync();
                await viewModel.GpnConnectCmd.Execute().ToTask();
                await WaitForCoreLeavingStartingAsync();
            }
            _connectionStarting = false;
            await SynchronizeConnectionStateAsync(forcePublish: true);
            // Bağlantı kurulamadıysa (koordinatör Failed / çekirdek Failed) nedeni
            // gösteren hata kartını dashboard'a bas.
            await TryPushConnectionFailureAsync();
        }
        finally
        {
            _connectionStarting = false;
            _connectionToggleGate.Release();
        }
    }

    /// <summary>
    /// "Gerçek ping" ölçümü: GPN listesindeki uygulamaların veritabanına kaydedilmiş
    /// sunucu uç noktalarına (uygulama çalışırken yakalanan gerçek oyun sunucusu
    /// adresleri) ping atar.
    ///
    ///   <paramref name="isBefore"/> true → program açılışında (doğrudan yol, "önce")
    ///   <paramref name="isBefore"/> false → GPN bağlantısı kurulunca (tünel yolu, "sonra")
    ///
    /// Ölçüm arka planda koşar; sonuçlar uygulama satırlarına (BeforePingMs /
    /// AfterPingMs) yazılır ve dashboard boost kartlarına basılır. Kayıt yoksa
    /// (uygulama hiç GPN ile çalışmamış) sessizce döner — ölçüm yalnızca veri
    /// varken anlamlıdır.
    /// </summary>
    private async Task MeasureRealPingAsync(bool isBefore, TimeSpan? delay = null)
    {
        try
        {
            if (delay is { } wait)
            {
                await Task.Delay(wait).ConfigureAwait(false);
            }

            // ViewModel (ReactiveWindow DependencyProperty) + ObservableCollection
            // yalnızca UI thread'inde okunabilir. Delay + ConfigureAwait(false) sonrası
            // burada pool thread'indeyiz — doğrudan okuma Dispatcher.VerifyAccess ile
            // "real ping measurement failed" üretir (canlı gözlenen). VM durumunu
            // dispatcher'a geri dönerek yakala, ölçümü (ağ) pool thread'inde yürüt.
            var appNames = await Dispatcher.InvokeAsync(() =>
            {
                var vm = ViewModel?.ConnectionViewModel;
                if (vm is null)
                {
                    return Array.Empty<string>();
                }
                return vm.Apps
                    .Where(a => a.EntryType == "app")
                    .Select(a => a.ProcessName.IsNotEmpty() ? a.ProcessName : a.Value)
                    .Where(n => n.IsNotEmpty())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }).Task.ConfigureAwait(false);
            if (appNames.Length == 0)
            {
                return;
            }

            var results = await GpnAppEndpointStore.Instance
                .MeasureRealPingAsync(appNames)
                .ConfigureAwait(false);
            if (results.Count == 0)
            {
                return;
            }

            var byApp = results.ToDictionary(r => r.AppName, StringComparer.OrdinalIgnoreCase);
            await Dispatcher.InvokeAsync(() =>
            {
                // VM durumunu burada (UI thread) TAZE oku — sonuçlar arka planda
                // alınırken liste değişmiş olabilir; bayat referansa yazılmaz.
                var vm = ViewModel?.ConnectionViewModel;
                if (vm is null)
                {
                    return;
                }
                foreach (var app in vm.Apps)
                {
                    var key = app.ProcessName.IsNotEmpty() ? app.ProcessName : app.Value;
                    if (!byApp.TryGetValue(key, out var result))
                    {
                        continue;
                    }
                    if (isBefore)
                    {
                        app.BeforePingMs = result.BestMs;
                        app.BeforePingText = FormatRealPing(result.BestMs);
                    }
                    else
                    {
                        app.AfterPingMs = result.BestMs;
                        app.AfterPingText = FormatRealPing(result.BestMs);
                        app.PingDeltaText = ComputePingDelta(app.BeforePingMs, result.BestMs);
                    }
                }
            });

            // PushMonitorSnapshotAsync reads ViewModel/VM bindings, so it must run
            // on the UI thread; this method resumes on a pool thread after
            // ConfigureAwait(false). Hop back to the dispatcher before pushing.
            await Dispatcher.InvokeAsync(() => _ = PushMonitorSnapshotAsync(force: true));
            var ok = results.Count(r => r.IsMeasured);
            DiagLog.Write($"GPN_REALPING {(isBefore ? "before" : "after")} apps={results.Count} ok={ok}");
        }
        catch (OperationCanceledException)
        {
            // kapanış — ölçüm best-effort
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN real ping measurement failed", ex);
        }
    }

    /// <summary>Ölçüm değerini gösterim metnine çevirir (ms >= 0 ise "42 ms", yoksa "—").</summary>
    private static string FormatRealPing(int ms) => ms >= 0 ? $"{ms} ms" : "—";

    /// <summary>Önce/sonra farkını gösterim metnine çevirir: "-24 ms" (iyileşme) / "+12 ms".</summary>
    private static string ComputePingDelta(int beforeMs, int afterMs)
    {
        if (beforeMs < 0 || afterMs < 0)
        {
            return string.Empty;
        }
        var delta = afterMs - beforeMs;
        return delta <= 0 ? $"{delta} ms" : $"+{delta} ms";
    }

    /// <summary>
    /// Quick connect/disconnect from the tray menu. Delegates to the same gated
    /// toggle the dashboard connect button uses, honoring the persisted routing
    /// mode (Manuel = GPN) and the effective capture transport.
    /// </summary>
    private async Task ToggleConnectionFromTrayAsync()
    {
        var configuredMode = AppManager.Instance.Config.ConnectionItem?.Mode
            ?? SplitTunnelViewModel.ModeOff;
        var requestedMode = configuredMode switch
        {
            SplitTunnelViewModel.ModeManual => "gpn",
            SplitTunnelViewModel.ModeVpn => "vpn",
            _ => "gpn",
        };
        await ToggleConnectionAsync(requestedMode, ReadTransport());
    }

    /// <summary>
    /// Changes between the real VPN and GPN routing modes while already connected.
    /// When disconnected, the frontend keeps the selected mode locally for the next connect.
    /// </summary>
    public async Task SetConnectionModeAsync(string mode)
    {
        if (!await _connectionToggleGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var connectionViewModel = ViewModel?.ConnectionViewModel;
            if (connectionViewModel is null || !ReadActualConnectionState())
            {
                return;
            }

            // Global VPN mode always uses TUN capture to route all traffic.
            if (mode == "vpn")
            {
                connectionViewModel.Transport = "tun";
            }

            var targetMode = mode == "gpn"
                ? SplitTunnelViewModel.ModeManual
                : SplitTunnelViewModel.ModeVpn;
            await ApplyConnectionModeAsync(connectionViewModel, targetMode);
        }
        finally
        {
            _connectionToggleGate.Release();
        }
    }

    /// <summary>
    /// Applies the three-state split-routing mode exposed by the Game Boost view.
    /// Unlike the legacy mode switch, this also accepts "off" while disconnected so
    /// the dashboard can stage a complete routing policy before connecting.
    /// </summary>
    public async Task SetDashboardModeAsync(string mode)
    {
        if (mode is not ("off" or "vpn" or "manual"))
        {
            return;
        }

        if (!await _connectionToggleGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var connectionViewModel = ViewModel?.ConnectionViewModel;
            if (connectionViewModel is null)
            {
                return;
            }

            if (mode == "off")
            {
                connectionViewModel.Transport = "";
                await ApplyConnectionModeAsync(connectionViewModel, SplitTunnelViewModel.ModeOff);
            }
            else
            {
                // GPN and Global VPN both require the TUN inbound for per-process
                // routing. Force the semantically correct transport up front so the
                // TUN elevation check fires before the core is started, matching
                // what SetConnectionModeAsync does for Global VPN.
                if (mode == "manual" || mode == "vpn")
                {
                    connectionViewModel.Transport = "tun";
                }
                else if (connectionViewModel.Transport is not ("tun" or "proxy"))
                {
                    connectionViewModel.Transport = ReadTransport();
                }
                var targetMode = mode == "manual"
                    ? SplitTunnelViewModel.ModeManual
                    : SplitTunnelViewModel.ModeVpn;
                await ApplyConnectionModeAsync(connectionViewModel, targetMode);
            }

            await PushMonitorSnapshotAsync(force: true);
        }
        finally
        {
            _connectionToggleGate.Release();
        }
    }

    /// <summary>
    /// Switches the capture transport between TUN and the system proxy, mirroring the
    /// native status bar's EnableTun + SysProxyType toggles. While connected the change
    /// is applied immediately; while disconnected it is remembered for the next connect.
    /// </summary>
    public async Task SetTransportAsync(string transport)
    {
        if (!await _connectionToggleGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var connectionViewModel = ViewModel?.ConnectionViewModel;
            if (connectionViewModel is null)
            {
                return;
            }

            if (transport == "tun" && !DashboardSettingsService.AllowEnableTun())
            {
                await NotifyConnectionErrorAsync(
                    "TUN mode requires administrator privileges. Relaunch as administrator to enable TUN.");
                return;
            }

            connectionViewModel.Transport = transport;

            if (ReadActualConnectionState())
            {
                await ApplyConnectionModeAsync(connectionViewModel, connectionViewModel.Mode);
            }
            else
            {
                // Remember the capture preference without touching the independent
                // system-proxy mode. The next connection will use this transport.
                // The raw AoGPN TUN flag mirrors the choice so the Settings view's
                // "Enable TUN mode" switch stays consistent with the Dashboard.
                var config = AppManager.Instance.Config;
                config.ConnectionItem ??= new();
                config.ConnectionItem.Transport = transport;
                config.TunModeItem.EnableTun = transport == "tun";
                await ConfigSaveQueue.SaveAndWaitAsync(config);
                await PushSettingsAsync();
            }

            await SynchronizeConnectionStateAsync(forcePublish: true);
            await PushSystemProxyStateAsync(force: true);
        }
        finally
        {
            _connectionToggleGate.Release();
        }
    }

    /// <summary>
    /// Enforces the persisted protocol strategy at connect time. When the preference is
    /// "auto" the active node is always kept. Otherwise, if the active node does not
    /// support the chosen strategy, the best matching node is selected automatically
    /// (using <see cref="ConnectionProtocolPolicy"/>) and a notice explains the switch;
    /// when no node supports the strategy a notice reports the fallback instead of
    /// silently connecting over a mismatched protocol.
    /// </summary>
    private async Task<string?> EnsureProtocolCompatibleAsync()
    {
        var config = AppManager.Instance.Config;
        var preference = ConnectionProtocolPreference.Normalize(config.ConnectionItem?.ProtocolPreference);
        if (preference == ConnectionProtocolPreference.Automatic)
        {
            return null;
        }

        var activeProfile = await ConfigHandler.GetDefaultServer(config);
        if (activeProfile is null)
        {
            return null;
        }

        if (ConnectionProtocolPolicy.Matches(activeProfile, preference))
        {
            return null;
        }

        List<ProfileItem>? allProfiles;
        try
        {
            allProfiles = await AppManager.Instance.ProfileItems(config.SubIndexId);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("Protocol strategy node lookup failed", ex);
            return null;
        }

        var best = ConnectionProtocolPolicy.SelectBest(allProfiles ?? [], preference);
        if (best is null)
        {
            return $"'{preference}' protokolü destekleyen düğüm bulunamadı — mevcut düğüm korundu.";
        }

        if (best.IndexId != config.IndexId)
        {
            await ConfigHandler.SetDefaultServerIndex(config, best.IndexId);
            ProfileExManager.Instance.TouchLastUsed(best.IndexId);
            await ProfileExManager.Instance.SaveTo();
            await PushNodeInfoAsync(force: true);
            await PushNodeListAsync();
            return $"'{preference}' stratejisi: destekleyen en iyi düğüme geçildi — {best.Remarks}";
        }

        return null;
    }

    /// <summary>
    /// Persists the dashboard protocol strategy preference. It only selects among
    /// capabilities the active profile supports and never rewrites profile credentials.
    /// The value is applied when the connection is (re)established rather than forcing
    /// a core restart for a preference-only change.
    /// </summary>
    public async Task SetProtocolPreferenceAsync(string preference)
    {
        var config = AppManager.Instance.Config;
        config.ConnectionItem ??= new();
        var normalized = ConnectionProtocolPreference.Normalize(preference);
        if (config.ConnectionItem.ProtocolPreference == normalized)
        {
            return;
        }

        config.ConnectionItem.ProtocolPreference = normalized;
        await ConfigSaveQueue.SaveAndWaitAsync(config);
        await PushSettingsAsync();
    }

    /// <summary>
    /// Persists the TUN network stack selected in the dashboard CAPTURE section
    /// (gvisor / system / mixed). The same field is editable from the settings
    /// form; this keeps the quick selector and the settings form in sync.
    /// </summary>
    public async Task SetTunStackAsync(string stack)
    {
        var config = AppManager.Instance.Config;
        config.TunModeItem ??= new();
        if (config.TunModeItem.Stack == stack)
        {
            return;
        }

        config.TunModeItem.Stack = stack;
        await ConfigSaveQueue.SaveAndWaitAsync(config);
        await PushSettingsAsync();
    }

    /// <summary>
    /// Persists the bounded core/tunnel recovery preference shown in the quick panel.
    /// </summary>
    public async Task SetAutoReconnectAsync(bool enabled)
    {
        var config = AppManager.Instance.Config;
        config.ConnectionItem ??= new();
        config.ConnectionItem.AutoReconnectEnabled = enabled;
        await ConfigSaveQueue.SaveAndWaitAsync(config);
        await PushSettingsAsync();
    }

    public async Task SetGpnRecoveryWatchAsync(bool enabled)
    {
        var config = AppManager.Instance.Config;
        config.GuiItem ??= new();
        config.GuiItem.GpnEnableRecoveryWatch = enabled;
        await ConfigSaveQueue.SaveAndWaitAsync(config);
        await PushSettingsAsync();
    }

    /// <summary>
    /// GPN otomatik sunucu değiştirmesini (failover/ping-pong) aç/kapat ve kalıcılaştır.
    /// Kapalıyken (varsayılan) GPN bağlantısı seçilen sunucuya takılı kalır — en stabil,
    /// kesintisiz deneyim için otomatik sunucu geçişi, V2rayTCP düşüşü ve kurtarma kapalıdır.
    /// </summary>
    public async Task SetGpnFailoverAsync(bool enabled)
    {
        var config = AppManager.Instance.Config;
        config.GuiItem ??= new();
        config.GuiItem.GpnEnableFailover = enabled;
        await ConfigSaveQueue.SaveAndWaitAsync(config);
        await PushSettingsAsync();
    }

    /// <summary>
    /// Çift Bağlantı (Bölünmüş Tünelleme) küresel launcher-bypass düğümünü (VLESS/Reality)
    /// dashboard yükünden kaydeder: zorunlu alanları (serverAddress / uuid / publicKey +
    /// geçerli port) doğrular ve <see cref="VlessProfileItem"/>'ı JSON olarak
    /// GuiItem.VlessBypassNodeJson'a yazar. Sonraki GPN WireGuard bağlantısı
    /// (GpnCoreLauncher) bu ayarı context'e taşır → mihomo YAML'ine ikincil
    /// "vless-launcher" outbound'u eklenir ve "warp" (BsGLauncher.exe) kuralları
    /// WARP SOCKS5 zinciri yerine o düğüme gider. Alanlar 64 karakterle sınırlı genel
    /// alıcılardan geçirilmez (Reality public key daha uzundur) — ham JSON'dan okunur.
    /// </summary>
    public async Task SetVlessBypassNodeAsync(JsonElement root)
    {
        string ReadString(string name) =>
            root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                ? (el.GetString() ?? string.Empty).Trim()
                : string.Empty;

        var name = ReadString("name");
        var serverAddress = ReadString("serverAddress");
        var uuid = ReadString("uuid");
        var publicKey = ReadString("publicKey");
        var shortId = ReadString("shortId");
        var serverName = ReadString("serverName");
        var serverPort = TryGetIntProperty(root, "serverPort");

        if (serverAddress.IsNullOrEmpty() || uuid.IsNullOrEmpty() || publicKey.IsNullOrEmpty())
        {
            await NotifyNodesOpAsync("VLESS bypass node is incomplete (serverAddress, uuid and publicKey are required)");
            return;
        }
        if (serverPort is null or <= 0 or >= 65536)
        {
            await NotifyNodesOpAsync("VLESS bypass node requires a valid server port (1-65535)");
            return;
        }

        var config = AppManager.Instance.Config;
        config.GuiItem ??= new GUIItem();
        config.GuiItem.VlessBypassNodeJson = JsonUtils.Serialize(new VlessProfileItem(
            Name: name.IsNotEmpty() ? name : "Launcher Bypass",
            ServerAddress: serverAddress,
            ServerPort: serverPort.Value,
            Uuid: uuid,
            PublicKey: publicKey,
            ShortId: shortId,
            ServerName: serverName), indented: false);
        await ConfigSaveQueue.SaveAndWaitAsync(config);
        await PushSettingsAsync();
        await PushVlessBypassNodeAsync();
        await NotifyNodesOpAsync("VLESS bypass node saved — applies on next GPN connect");
    }

    /// <summary>
    /// Çift Bağlantı (Bölünmüş Tünelleme) küresel launcher-bypass düğümünü ham bir
    /// <c>vless://</c> Reality paylaşım bağlantısından kaydeder: kanonik ayrıştırıcı
    /// (<see cref="FmtHandler.ResolveConfig"/>) ile çözülür, zorunlu Reality alanları
    /// (Address / Port / UUID / pbk) doğrulanır ve <see cref="VlessProfileItem"/> olarak
    /// GuiItem.VlessBypassNodeJson'a yazılır. Boş URI gönderilirse ayar temizlenir —
    /// legacy WARP davranışı geri gelir (Çift Bağlantı kapalı).
    /// </summary>
    public async Task SetVlessBypassFromUriAsync(JsonElement root)
    {
        var uri = root.TryGetProperty("uri", out var el) && el.ValueKind == JsonValueKind.String
            ? (el.GetString() ?? string.Empty).Trim()
            : string.Empty;

        var config = AppManager.Instance.Config;
        config.GuiItem ??= new GUIItem();

        if (uri.IsNullOrEmpty())
        {
            config.GuiItem.VlessBypassNodeJson = null;
            await ConfigSaveQueue.SaveAndWaitAsync(config);
            await PushSettingsAsync();
            await PushVlessBypassNodeAsync();
            await NotifyNodesOpAsync("VLESS bypass cleared — legacy WARP routing restored");
            return;
        }

        var profile = FmtHandler.ResolveConfig(uri, out var msg);
        if (profile is null)
        {
            await NotifyNodesOpAsync($"Invalid share link: {msg}");
            return;
        }
        if (profile.ConfigType != EConfigType.VLESS)
        {
            await NotifyNodesOpAsync("Only vless:// Reality links are supported for the launcher bypass");
            return;
        }
        if (profile.Port is <= 0 or >= 65536
            || profile.Address.IsNullOrEmpty()
            || profile.Password.IsNullOrEmpty()
            || profile.PublicKey.IsNullOrEmpty())
        {
            await NotifyNodesOpAsync("VLESS link is missing required Reality fields (server address, port, uuid, public key)");
            return;
        }

        config.GuiItem.VlessBypassNodeJson = JsonUtils.Serialize(new VlessProfileItem(
            Name: profile.Remarks.IsNotEmpty() ? profile.Remarks : "Launcher Bypass",
            ServerAddress: profile.Address,
            ServerPort: profile.Port,
            Uuid: profile.Password,
            PublicKey: profile.PublicKey,
            ShortId: profile.ShortId,
            ServerName: profile.Sni,
            Flow: profile.GetProtocolExtra().Flow.IsNotEmpty() ? profile.GetProtocolExtra().Flow : "xtls-rprx-vision",
            Fingerprint: profile.Fingerprint.IsNotEmpty() ? profile.Fingerprint : "chrome"), indented: false);
        await ConfigSaveQueue.SaveAndWaitAsync(config);
        await PushSettingsAsync();
        await PushVlessBypassNodeAsync();
        await NotifyNodesOpAsync("VLESS bypass node saved — applies on next GPN connect");
    }

    /// <summary>
    /// Kayıtlı küresel launcher-bypass düğümünü dashboard'a gönderir
    /// (window.setVlessBypassNode). Ayar boş ya da geçersizse null iletilir — renderer
    /// alanları boş duruma getirir (Çift Bağlantı kapalı = legacy WARP davranışı).
    /// </summary>
    public async Task PushVlessBypassNodeAsync()
    {
        if (!_webViewReady)
        {
            return;
        }

        try
        {
            var json = AppManager.Instance.Config.GuiItem?.VlessBypassNodeJson;
            var node = json.IsNotEmpty() ? JsonUtils.Deserialize<VlessProfileItem>(json) : null;
            object? payload = null;
            if (node is not null)
            {
                payload = new
                {
                    node.Name,
                    node.ServerAddress,
                    node.ServerPort,
                    node.Uuid,
                    node.PublicKey,
                    node.ShortId,
                    node.ServerName,
                    node.Flow,
                    node.Fingerprint,
                };
            }
            await ExecuteScriptSafelyAsync($"window.setVlessBypassNode?.({JsonSerializer.Serialize(payload, RouteTestJsonOptions)});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN vless-bypass push failed", ex);
        }
    }

    /// <summary>
    /// Mevcut GpnCaptureItem ayarlarını dashboard "WinDivert kuyruk" kartına gönderir
    /// (tüm alanlar — yalnızca kullanıcı ayarları, özel veri yok).
    /// </summary>
    public async Task PushGpnCaptureSettingsAsync()
    {
        if (!_webViewReady)
        {
            return;
        }

        try
        {
            var settings = AppManager.Instance.Config.GpnCaptureItem ?? new GpnCaptureItem();
            var json = JsonSerializer.Serialize(new
            {
                settings.EnableQueueLen,
                settings.EnableQueueTime,
                settings.EnableQueueSize,
                settings.QueueLen,
                settings.QueueTime,
                settings.QueueSize,
                settings.Layer,
                settings.Direction,
            }, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setGpnCaptureSettings?.({json});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-capture-settings push failed", ex);
        }
    }

    /// <summary>
    /// Dashboard "WinDivert kuyruk" kartından gelen set_gpn_capture_settings yükünü
    /// doğrular (saf <see cref="GpnCaptureSettingsPatch.Apply"/> — aralık sınırlama
    /// ServiceLib'de, test edilebilir) ve config'e yazar. Sonraki yakalama başlangıcı
    /// (GpnCaptureLoop) bu değerleri kullanır.
    /// </summary>
    public async Task SetGpnCaptureSettingsAsync(JsonElement root)
    {
        var patch = new GpnCaptureSettingsPatch(
            QueueLen: ToUintOrNull(TryGetIntProperty(root, "queueLen")),
            QueueTime: ToUintOrNull(TryGetIntProperty(root, "queueTime")),
            QueueSize: ToUintOrNull(TryGetIntProperty(root, "queueSize")),
            EnableQueueLen: DashboardMessageDispatcher.TryGetBooleanProperty(root, "enableQueueLen", out var enableLen) ? enableLen : null,
            EnableQueueTime: DashboardMessageDispatcher.TryGetBooleanProperty(root, "enableQueueTime", out var enableTime) ? enableTime : null,
            EnableQueueSize: DashboardMessageDispatcher.TryGetBooleanProperty(root, "enableQueueSize", out var enableSize) ? enableSize : null,
            Layer: TryGetIntProperty(root, "layer"),
            Direction: TryGetIntProperty(root, "direction"));

        var config = AppManager.Instance.Config;
        config.GpnCaptureItem = patch.Apply(config.GpnCaptureItem);
        await ConfigSaveQueue.SaveAndWaitAsync(config);
        await PushGpnCaptureSettingsAsync();
        await PushSettingsAsync();
    }

    /// <summary>
    /// Mevcut GpnWintunItem ayarlarını dashboard "Wintun adapter" kartına gönderir
    /// (adapter ad ön eki + halka tampon kapasitesi). Bir sonraki WireGuard
    /// bağlantısında köprü bu değerlerle açılır.
    /// </summary>
    public async Task PushGpnWintunSettingsAsync()
    {
        if (!_webViewReady)
        {
            return;
        }

        try
        {
            var settings = AppManager.Instance.Config.GpnWintunItem ?? new GpnWintunItem();
            var json = JsonSerializer.Serialize(new
            {
                AdapterName = settings.AdapterName,
                RingCapacity = settings.RingCapacity,
            }, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setGpnWintunSettings?.({json});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-wintun-settings push failed", ex);
        }
    }

    /// <summary>
    /// Dashboard "Wintun adapter" kartından gelen set_gpn_wintun_settings yükünü
    /// doğrular (saf <see cref="GpnWintunSettingsPatch.Apply"/> — adapter adı
    /// sanitleştirilir, kapasite sınırlanıp 2'nin katına yuvarlanır) ve config'e
    /// yazar. Sonraki WireGuard bağlantısında (GpnCaptureBridge köprü açılışı)
    /// bu değerler uygulanır.
    /// </summary>
    public async Task SetGpnWintunSettingsAsync(JsonElement root)
    {
        var patch = new GpnWintunSettingsPatch(
            AdapterName: DashboardMessageDispatcher.TryGetStringProperty(root, "adapterName", out var adapterName) ? adapterName : null,
            RingCapacity: ToUintOrNull(TryGetIntProperty(root, "ringCapacity")));

        var config = AppManager.Instance.Config;
        config.GpnWintunItem = patch.Apply(config.GpnWintunItem);
        await ConfigSaveQueue.SaveAndWaitAsync(config);
        await PushGpnWintunSettingsAsync();
        await PushSettingsAsync();
    }

    private static int? TryGetIntProperty(JsonElement objectElement, string propertyName)
    {
        if (!objectElement.TryGetProperty(propertyName, out var property))
        {
            return null;
        }
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }
        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static uint? ToUintOrNull(int? value) => value is >= 0 ? (uint?)value : null;

    public async Task SetSystemProxyModeAsync(ESysProxyType requestedType)
    {
        if (!await _connectionToggleGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var config = AppManager.Instance.Config;
            config.SystemProxyItem ??= new();
            config.SystemProxyItem.SysProxyType = requestedType;

            // Persist the user's independent AoGPN-style choice first, then reconcile:
            // when the connection is off and Set/PAC is chosen, a proxy-only core is
            // started so the OS proxy points at a live local listener immediately.
            await ConfigSaveQueue.SaveAndWaitAsync(config);
            var applied = await _proxyOnlyService.ReconcileAsync(config);

            var status = StatusBarViewModel.Instance;
            status.SystemProxySelected = (int)requestedType;
            status.BlSystemProxyClear = requestedType == ESysProxyType.ForcedClear;
            status.BlSystemProxySet = requestedType == ESysProxyType.ForcedChange;
            status.BlSystemProxyNothing = requestedType == ESysProxyType.Unchanged;
            status.BlSystemProxyPac = requestedType == ESysProxyType.Pac;

            await PushSystemProxyStateAsync(force: true);
            await PushSettingsAsync();
            UpdateTrayStatus();
            await NotifySystemProxyResultAsync(
                applied,
                requestedType,
                ComposeSystemProxyResultMessage(requestedType, applied));
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN system proxy transition failed", ex);
            await NotifySystemProxyResultAsync(false, requestedType, "System proxy update failed");
        }
        finally
        {
            _connectionToggleGate.Release();
        }
    }

    public async Task ToggleSystemProxyAsync()
    {
        var current = AppManager.Instance.Config.SystemProxyItem?.SysProxyType
            ?? ESysProxyType.ForcedClear;
        await SetSystemProxyModeAsync(SystemProxyPolicy.Toggle(current));
    }

    private async Task ApplyConnectionModeAsync(
        SplitTunnelViewModel connectionViewModel,
        int targetMode)
    {
        WarnOnForeignTunnelBeforeConnect(targetMode);
        connectionViewModel.Mode = targetMode;
        // A real connection is taking over the core; the reload (LoadCore) stops the
        // previous process, including a running proxy-only core. Release ownership so
        // this service never stops the connection's core afterwards.
        _proxyOnlyService.ReleaseOwnership();

        try
        {
            // ApplyCmd writes the managed routing rules, persists ConnectionItem,
            // updates TUN/system-proxy settings, and publishes the standard reload.
            await connectionViewModel.ApplyCmd.Execute().ToTask();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("WebView2 connection transition failed", ex);
        }

        // Surface the TUN elevation requirement instead of failing silently, and
        // give the dashboard a path to relaunch as administrator.
        if (connectionViewModel.NeedAdmin)
        {
            await NotifyConnectionErrorAsync(
                "TUN mode requires administrator privileges. Relaunch as administrator to enable TUN.");
        }

        // Publish the effective state after ApplyCmd has completed. If validation or
        // elevation rejected the change, the persisted mode/core state remains unchanged.
        await SynchronizeConnectionStateAsync(forcePublish: true);

        // After a disconnect (or a rejected mode change back to Off) the proxy-only
        // core may need to come back up so the system proxy keeps working. KESİNTİSİZ
        // (soft) mod geçişinde — örn. bağlıyken Off'a geçişte — GPN tüneli durmaz ve
        // hâlâ yerel SOCKS portunu dinler; bu durumda proxy-only çekirdek başlatılmaz
        // (aynı porta ikinci çekirdek çakışırdı).
        if (!IsGpnTunnelActive())
        {
            try
            {
                await _proxyOnlyService.ReconcileAsync(AppManager.Instance.Config);
                await PushSystemProxyStateAsync(force: true);
            }
            catch (Exception ex)
            {
                Logging.SaveLog("AoGPN proxy-only reconcile failed", ex);
            }
        }

        UpdateTrayStatus();
    }

    /// <summary>
    /// GPN koordinatör tüneli hâlâ ayakta mı? Kesintisiz (soft) mod geçişi çekirdeği
    /// durdurmadığı için Mode == Off olsa bile GPN tüneli canlı kalabilir — tray/pill
    /// ve proxy-only kararları bunu gerçek bir "bağlantı var" olarak görmeli.
    /// </summary>
    private bool IsGpnTunnelActive()
    {
        var snapshot = ViewModel?.GpnCoordinatorSnapshot;
        return snapshot?.State is GpnConnectionState.Connected or GpnConnectionState.Connecting;
    }

    /// <summary>
    /// Reads the effective connection state from AoGPN's persisted routing mode and
    /// running core, rather than from the renderer's last requested value.
    /// </summary>
    private bool ReadActualConnectionState()
    {
        var connectionViewModel = ViewModel?.ConnectionViewModel;
        var configuredMode = AppManager.Instance.Config.ConnectionItem?.Mode
            ?? SplitTunnelViewModel.ModeOff;

        if (connectionViewModel is null
            || configuredMode == SplitTunnelViewModel.ModeOff)
        {
            return false;
        }

        // A process that merely exists is not a connected tunnel. CoreManager
        // publishes Ready only after the local listener has answered SOCKS5.
        var health = AppManager.Instance.CoreEngineHost?.GetHealth(CoreHealthRole.Main);
        if (health is not null)
        {
            return health.State == CoreHealthState.Ready;
        }

        return AppManager.Instance.IsRunningCore(ECoreType.Xray)
            || AppManager.Instance.IsRunningCore(ECoreType.sing_box)
            || AppManager.Instance.IsRunningCore(ECoreType.openvpn);
    }

    /// <summary>
    /// Keeps the tray status line in sync with the effective runtime state:
    /// a live connection takes precedence, then a proxy-only core, then idle.
    /// </summary>
    private void UpdateTrayStatus()
    {
        var status = StatusBarViewModel.Instance;
        if (ReadActualConnectionState())
        {
            status.TrayStatusLine = ResUI.TrayStatusConnected;
            status.TrayStatusState = 2;
        }
        else if (_proxyOnlyService.IsRunning)
        {
            status.TrayStatusLine = ResUI.TrayStatusProxyOnly;
            status.TrayStatusState = 1;
        }
        else
        {
            status.TrayStatusLine = ResUI.TrayStatusIdle;
            status.TrayStatusState = 0;
        }
    }

    /// <summary>
    /// Before AoGPN starts its own tunnel, check for foreign VPN state that would
    /// collide with it (another client's TUN adapter, a foreign listener on the
    /// local proxy port, or a well-known VPN client such as WireGuard for Windows)
    /// and warn the user. Third-party VPN clients are never terminated — closing
    /// them is the user's decision.
    /// </summary>
    private void WarnOnForeignTunnelBeforeConnect(int targetMode)
    {
        if (targetMode == SplitTunnelViewModel.ModeOff)
        {
            return;
        }

        var localPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        var result = _foreignTunnelDetector.Detect(localPort);
        if (!result.HasConflicts)
        {
            return;
        }

        // Conflicting foreign VPN state detected — warn only. AoGPN never closes
        // another VPN client; the user decides whether to close it first.
        var details = new List<string>();
        if (result.HasTunConflict)
        {
            details.Add(string.Format(
                ResUI.ForeignTunnelDetailTun, string.Join(", ", result.TunAdapterNames)));
        }
        if (result.HasPortConflict)
        {
            details.Add(string.Format(
                ResUI.ForeignTunnelDetailPort, localPort));
        }
        if (result.ForeignProcessNames.Count > 0)
        {
            details.Add(string.Join(", ", result.ForeignProcessNames));
        }

        NoticeManager.Instance.SendMessageAndEnqueue(string.Format(
            ResUI.ForeignTunnelWarning, string.Join("; ", details)));
    }

    /// <summary>
    /// Surfaces core selection for REALITY nodes: when the main core failed and the
    /// active node is a REALITY node that ran on sing-box, offer a "Switch to Xray"
    /// action that only runs with the user's approval (TUN off), or surface a notice
    /// explaining that the switch is blocked by TUN. Fires once per node per TUN state.
    /// </summary>
    private async Task SuggestRealityCoreFallbackAsync()
    {
        var config = AppManager.Instance.Config;
        var mode = config.ConnectionItem?.Mode ?? SplitTunnelViewModel.ModeOff;
        if (mode == SplitTunnelViewModel.ModeOff)
        {
            return;
        }

        var health = AppManager.Instance.CoreEngineHost?.GetHealth(CoreHealthRole.Main);
        if (health is null || health.State != CoreHealthState.Failed)
        {
            return;
        }

        ProfileItem? node;
        try
        {
            node = await AppManager.Instance.GetProfileItem(config.IndexId);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN REALITY core fallback node lookup failed", ex);
            return;
        }
        if (node is null)
        {
            return;
        }

        var tunEnabled = config.TunModeItem.EnableTun;
        if (!_realityFallbackAdvisedNodes.Add($"{node.IndexId}|{tunEnabled}"))
        {
            return;
        }

        var advice = RealityCoreFallbackAdvisor.Evaluate(
            node,
            failedCoreType: health.CoreType,
            tunEnabled: tunEnabled,
            connectionUp: ReadActualConnectionState(),
            modeOff: false);
        if (advice == RealityFallbackAdvice.None)
        {
            return;
        }

        if (advice == RealityFallbackAdvice.SwitchToXray)
        {
            // The node is switched only with the user's approval: surface an
            // actionable notification and wait for the "Switch to Xray" action
            // instead of silently changing the core type under the user.
            NoticeManager.Instance.EnqueueAction(
                ResUI.RealityCoreSwitchToXrayPrompt,
                ResUI.RealityCoreSwitchToXrayButton,
                () => _ = ApplyRealityXraySwitchAsync(node.IndexId));
        }
        else
        {
            NoticeManager.Instance.Enqueue(ResUI.RealityCoreSuggestXray);
        }
    }

    /// <summary>
    /// Applies the user-approved REALITY core switch: re-reads the node by id (the
    /// notification may be acted on long after it was shown), switches it to the
    /// Xray core, persists the change and restarts the tunnel.
    /// </summary>
    private async Task ApplyRealityXraySwitchAsync(string indexId)
    {
        ProfileItem? node;
        try
        {
            node = await AppManager.Instance.GetProfileItem(indexId);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN REALITY core fallback node lookup failed", ex);
            return;
        }
        if (node is null || !RealityCoreFallbackAdvisor.IsRealityNode(node))
        {
            return;
        }

        RealityCoreFallbackAdvisor.ApplyXraySwitch(node);
        try
        {
            await SQLiteHelper.Instance.UpdateAsync(node);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN REALITY core fallback switch persist failed", ex);
            return;
        }

        NoticeManager.Instance.Enqueue(ResUI.RealityCoreSwitchedToXray);
        await RetryFallbackConnectionAsync();
    }

    /// <summary>
    /// Re-runs the connection flow after the REALITY core switch so the tunnel is
    /// restarted with the node's new Xray core type.
    /// </summary>
    private async Task RetryFallbackConnectionAsync()
    {
        var connectionViewModel = ViewModel?.ConnectionViewModel;
        if (connectionViewModel is null)
        {
            return;
        }

        var requestedMode = connectionViewModel.Mode == SplitTunnelViewModel.ModeVpn ? "vpn" : "gpn";
        await ToggleConnectionAsync(requestedMode, connectionViewModel.Transport);
    }

    /// <summary>Pushes the effective AoGPN state to JavaScript when it changes.</summary>
    private async Task SynchronizeConnectionStateAsync(bool forcePublish = false)
    {
        // While a connect is in flight the core may not have reached Ready yet
        // (it is still handshaking). Never downgrade the published state to
        // "disconnected" and back again during that window — that race is the
        // connected -> canceled -> reconnected flicker. Report connecting instead.
        if (_connectionStarting)
        {
            if (Volatile.Read(ref _connectionState))
            {
                // Already reported connected; leave it until the core settles.
                return;
            }
            await SendConnectionStateAsync();
            return;
        }

        var actualState = ReadActualConnectionState();
        var previousState = Volatile.Read(ref _connectionState);
        Volatile.Write(ref _connectionState, actualState);

        if (forcePublish || actualState != previousState)
        {
            await SendConnectionStateAsync();
        }
    }

    /// <summary>
    /// Waits (bounded) until the main core has settled into a terminal state
    /// (Ready / Degraded / Failed) while a connect is in flight. The GPN connect
    /// performs server selection (ICMP + UDP probes) BEFORE the core is launched,
    /// so during that window the core is not yet Starting — returning early there
    /// made the dashboard publish "disconnected" mid-connect and then "connected"
    /// once the core finished (the connect → drop → reconnect flicker). Waiting
    /// while <see cref="_connectionStarting"/> is true keeps the button on
    /// "connecting" until the core is genuinely ready (or genuinely failed).
    /// </summary>
    private async Task WaitForCoreLeavingStartingAsync(int timeoutSeconds = 20)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < timeoutSeconds)
        {
            // Connect was aborted (user disconnected or the flow was superseded) —
            // stop waiting so the disconnect state can be published promptly.
            if (!_connectionStarting)
            {
                return;
            }

            // GPN koordinatörü başarısız olduysa (seçim/launcher hatası — çekirdek
            // hiç başlamamış olabilir) hazır-beklemeye girmeyelim: 20 sn yerine hata
            // kartı hemen yayınlanır.
            if (ViewModel?.GpnCoordinatorSnapshot is { State: GpnConnectionState.Failed })
            {
                return;
            }

            var health = AppManager.Instance.CoreEngineHost?.GetHealth(CoreHealthRole.Main);
            if (health is null)
            {
                // Core not created yet — the GPN selection (probe) phase is still
                // running. Keep waiting; do not publish a premature disconnect.
                await Task.Delay(200);
                continue;
            }

            if (health.State is CoreHealthState.Ready
                or CoreHealthState.Degraded
                or CoreHealthState.Failed)
            {
                return; // settled: connected, degraded, or failed — publish as-is.
            }
            await Task.Delay(200);
        }
    }

    public void HandleAppControl(string command)
    {
        switch (command)
        {
            case "minimize":
                if (_config.UiItem.Minimize2Tray)
                {
                    TryCreateTrayIcon();
                    _trayBehavior.HandleMinimize(_config);
                }
                else
                {
                    WindowState = WindowState.Minimized;
                }
                break;

            case "close":
                Logging.SaveLog($"Window close requested via title bar (hide2TrayWhenClose={_config.UiItem.Hide2TrayWhenClose})");
                if (_trayBehavior.ShouldHideOnClose(_config))
                {
                    TryCreateTrayIcon();
                    _trayBehavior.CloseToTray();
                }
                else
                {
                    // Real exit — mirror the native caption X: close the window
                    // IMMEDIATELY and let ExitApplicationSafelyAsync clear the OS
                    // proxy / flush state / stop the core in the background before
                    // it shuts the application down. Previously the window stayed
                    // open until those steps finished (each bounded at 20 s), so a
                    // slow or wedged step made the close look broken — the window
                    // simply never went away. Close() lets MainWindow_Closing and
                    // MainWindow_Closed run their normal teardown (window state is
                    // still saved — the close request itself is the flush signal).
                    _allowClose = true;
                    _ = ExitApplicationSafelyAsync();
                    Close();
                }
                break;

            case "drag":
                BeginWindowDrag();
                break;

            case "maximize_toggle":
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
                _ = PushWindowStateAsync();
                break;

            case "reboot_as_admin":
                _ = AppManager.Instance.RebootAsAdmin();
                break;

            case "reload":
                // Regenerates the sing-box config from the current database state and
                // restarts the core — the fix for stale-rule drift. Goes through the
                // native ReloadCmd so the routing pipeline (and rule persistence) stays
                // authoritative.
                if (ViewModel?.ReloadCmd is { } reloadCmd)
                {
                    // Executing a disabled ReactiveCommand is a no-op, so the
                    // CanExecute gate (bound to menuReload.IsEnabled) is respected
                    // implicitly.
                    reloadCmd.Execute().Subscribe();
                }
                break;
        }
    }

    /// <summary>Pushes the current maximized state so the HTML title bar icon stays truthful.</summary>
    private async Task PushWindowStateAsync()
    {
        await ExecuteScriptSafelyAsync(
            $"window.setWindowMaximized({JsonSerializer.Serialize(WindowState == WindowState.Maximized)});");
    }

    private bool _lastMaximizedState;
    private bool _windowStatePublished;

    /// <summary>
    /// Publishes the maximized state only when it changed, so external transitions
    /// (Win+Up/Down, snapping, double-click on the drag region) stay in sync with
    /// the renderer's title bar icon.
    /// </summary>
    private async Task SynchronizeWindowStateAsync()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        if (_windowStatePublished && isMaximized == _lastMaximizedState)
        {
            return;
        }

        _lastMaximizedState = isMaximized;
        _windowStatePublished = true;
        await PushWindowStateAsync();
    }

    /// <summary>
    /// Pushes the current WPF theme mapping to the WebView2 dashboard so its CSS
    /// variable palette (nebula/inferno/venom/…) stays in sync with the WPF theme.
    /// </summary>
    internal static bool TryMapWebThemeToWpf(string webTheme, out string wpfTheme)
    {
        wpfTheme = webTheme switch
        {
            "nebula" => nameof(ETheme.Dark),
            "plasma" => nameof(ETheme.Dusk),
            "cryo" => nameof(ETheme.NightSky),
            "matrix" => nameof(ETheme.Aquatic),
            "inferno" => nameof(ETheme.Desert),
            "phantom" => nameof(ETheme.Light),
            "crimson" => nameof(ETheme.Crimson),
            "velocity" => nameof(ETheme.Velocity),
            "venom" => nameof(ETheme.Venom),
            "synthwave" => nameof(ETheme.Synthwave),
            "cyberpunk" => nameof(ETheme.Cyberpunk),
            _ => string.Empty,
        };
        return wpfTheme.IsNotEmpty();
    }

    private async Task PushThemeAsync(string webViewThemeId)
    {
        await ExecuteScriptSafelyAsync(
            $"if(typeof applyTheme==='function'){{applyTheme('{webViewThemeId}',false)}}");
    }

    /// <summary>Wave 2: <see cref="DashboardSettingsService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task PushEffectsTierAsync() => _settingsService.PushEffectsTierAsync();
    /// <summary>
    /// One-shot explanation when the startup guard auto-disabled hardware
    /// acceleration (no usable GPU path, or the crash budget was exceeded):
    /// tells the user why the toggle is off and how to restore it. The flag is
    /// cleared here so the notice shows exactly once.
    /// </summary>
    private async Task ShowHwaFallbackNoticeIfNeededAsync()
    {
        var config = AppManager.Instance.Config;
        if (!config.GuiItem.HwaAutoDisabledNotice)
        {
            return;
        }

        config.GuiItem.HwaAutoDisabledNotice = false;
        ConfigSaveQueue.RequestSave(config);

        AppEvents.SendSnackMsgRequested.Publish(
            "Hardware acceleration was disabled automatically after repeated graphics " +
            "failures. Update your GPU driver, then re-enable it in Settings.");
        await Task.CompletedTask;
    }

    /// <summary>Wave 2: <see cref="DashboardSettingsService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task PushLanguageAsync() => _settingsService.PushLanguageAsync();
    /// <summary>Wave 2: <see cref="DashboardSettingsService"/> delegasyonu — gövde servise taşındı.</summary>
    private Task PushAppInfoAsync() => _settingsService.PushAppInfoAsync();
    /// <summary>
    /// Resolves EXE file names dropped onto the Game Boost page to full paths.
    /// Checks running processes first, then falls back to common install locations.
    /// </summary>
    internal static async Task<string[]> ResolveDropFilePathsAsync(string[] exeNames)
    {
        var resolved = new List<string>();
        foreach (var name in exeNames)
        {
            var processName = Path.GetFileNameWithoutExtension(name);
            try
            {
                var existing = Process.GetProcessesByName(processName);
                if (existing.Length > 0)
                {
                    var path = existing[0].MainModule?.FileName;
                    if (path.IsNotEmpty() && File.Exists(path))
                    {
                        resolved.Add(path);
                    }
                    foreach (var p in existing) { try { p.Dispose(); } catch { } }
                    if (resolved.Count > 0 && resolved[^1] == path) continue;
                }
            }
            catch { }

            // Fallback: search common game install paths
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steamapps", "common"),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            };
            foreach (var dir in candidates.Where(Directory.Exists))
            {
                try
                {
                    var found = Directory.GetFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
                    if (found.IsNotEmpty())
                    {
                        resolved.Add(found);
                        break;
                    }
                }
                catch { }
            }
        }
        return resolved.ToArray();
    }

    /// <summary>
    /// Starts a title-bar drag from the HTML drag region. While the window is
    /// restored, the native caption move loop provides both moving and the standard
    /// drag-to-top maximize gesture. From maximized the native loop cannot restore
    /// this borderless window, so the restore-on-drag is handled manually.
    /// </summary>


    private void BeginWindowDrag()
    {
        if (_isClosing)
        {
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            StartManualDrag();
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(handle, WmNcLButtonDown, HtCaption, IntPtr.Zero);
    }

    private bool _manualDrag;
    private int _manualOffsetX;
    private int _manualOffsetY;
    private int _manualRestoreLeft;
    private int _manualRestoreTop;

    /// <summary>
    /// Captures the mouse and tracks the cursor so a maximized window restores and
    /// follows the pointer (drag-down), and re-maximizes when dragged back to the
    /// top edge. A plain click without movement leaves the window maximized.
    /// </summary>
    private void StartManualDrag()
    {
        if (_isClosing || _manualDrag)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetCursorPos(out var cursor))
        {
            return;
        }

        // Anchor the follow-offset to the restore position converted to physical
        // pixels, so the window keeps the grabbed point under the cursor.
        var scale = GetDpiForWindow(handle) / 96.0;
        _manualRestoreLeft = (int)(RestoreBounds.Left * scale);
        _manualRestoreTop = (int)(RestoreBounds.Top * scale);
        _manualOffsetX = cursor.X - _manualRestoreLeft;
        _manualOffsetY = cursor.Y - _manualRestoreTop;

        SetCapture(handle);
        _manualDrag = true;
    }

    private void OnManualDragMove()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetCursorPos(out var cursor))
        {
            return;
        }

        // Dragging all the way to the top re-maximizes (mirrors the native gesture).
        if (cursor.Y <= 8)
        {
            ReleaseCapture();
            _manualDrag = false;
            WindowState = WindowState.Maximized;
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            SetWindowPos(handle, IntPtr.Zero, _manualRestoreLeft, _manualRestoreTop, 0, 0, SwpNoSize | SwpNoZOrder);
        }

        SetWindowPos(handle, IntPtr.Zero, cursor.X - _manualOffsetX, cursor.Y - _manualOffsetY, 0, 0, SwpNoSize | SwpNoZOrder);
    }

    /// <summary>
    /// GPN koordinatörü durum değiştirdiğinde ana bağlantı durumunu eşitler. Çekirdek
    /// Ready olunca (Connected) buton anında "Bağlantıyı Kes" olur; bağlantı kesilince
    /// (Disconnected) "Bağlan"a döner. Connecting ara durumu, <see cref="SynchronizeConnectionStateAsync"/>
    /// içindeki _connectionStarting yoluyla zaten "connecting" olarak yayınlanır — burada
    /// yalnızca kesin durumlar (bağlı/kesik) işlenir.
    /// </summary>
    private async Task OnGpnConnectionSnapshotAsync(GpnConnectionSnapshot snapshot)
    {
        switch (snapshot.State)
        {
            case GpnConnectionState.Connecting:
                // Yeni deneme başladı — eski başarısızlık hata kartına taşınmasın.
                _lastGpnFailedSnapshot = null;
                break;
            case GpnConnectionState.Connected:
                Volatile.Write(ref _connectionState, true);
                _lastGpnFailedSnapshot = null;
                await SendConnectionStateAsync();

                // Gerçek ping "sonra" ölçümü: tünel kurulunca aynı uç noktalara
                // tünel yolundan ping atılır ve açılıştaki "önce" değeriyle
                // karşılaştırılır (öncesi/sonrası). Tünel oturana kadar kısa
                // gecikmeyle arka planda koşar (best-effort).
                _ = MeasureRealPingAsync(isBefore: false, delay: TimeSpan.FromSeconds(3));
                break;
            case GpnConnectionState.Disconnected:
                Volatile.Write(ref _connectionState, false);
                // GPN Bağlan butonu bağlıyken ikinci basışta (toggle) koordinatör
                // Disconnected'a geçer; bekleyen WaitForCoreLeavingStartingAsync 20 sn
                // beklemesin diye bağlanma bayrağını da kapat (disconnect sonu).
                Volatile.Write(ref _connectionStarting, false);
                await SendConnectionStateAsync();
                break;
            case GpnConnectionState.Failed:
                // Seçim/launcher hatası — çekirdek hiç başlamamış olabilir; hata
                // kartı için son Failed anlık görüntüsünü sakla (mesaj + zaman).
                _lastGpnFailedSnapshot = snapshot;
                break;
        }
    }

    private async Task SendConnectionStateAsync()
    {
        var connectedJson = JsonSerializer.Serialize(Volatile.Read(ref _connectionState));
        var connectingJson = JsonSerializer.Serialize(_connectionStarting);
        var configuredMode = AppManager.Instance.Config.ConnectionItem?.Mode;
        var modeJson = JsonSerializer.Serialize(
            configuredMode == SplitTunnelViewModel.ModeManual ? "gpn" : "vpn");
        await ExecuteScriptSafelyAsync(
            $"window.setConnectionState({connectedJson}, {modeJson}, {connectingJson});");
        await ExecuteScriptSafelyAsync(
            $"window.setTransport({JsonSerializer.Serialize(ReadTransport())});");
        // Lets the dashboard lock CONNECT up front when TUN is selected but the
        // session is not elevated, instead of failing only after the press.
        await ExecuteScriptSafelyAsync(
            $"window.setAdminState({JsonSerializer.Serialize(Utils.IsAdministrator())});");
        await PushSystemProxyStateAsync();

        // Keep the node card in sync with the selected profile whenever the
        // effective connection state is (re)published.
        await PushNodeInfoAsync(force: true);

        // Push the real public IP state (ISP IP vs tunnel IP) so the dashboard
        // never shows fake/simulated connection data.
        _ = Task.Run(async () =>
        {
            try { await CheckIpAsync(); }
            catch { /* best-effort — the dashboard shows its last-known state */ }
        });
    }

    /// <summary>
    /// Derives the effective capture transport. A pending dashboard override wins,
    /// otherwise the persisted TUN flag decides (TUN vs system proxy).
    /// </summary>
    private string ReadTransport()
    {
        var transportOverride = ViewModel?.ConnectionViewModel?.Transport;
        if (transportOverride is "tun" or "proxy")
        {
            return transportOverride;
        }

        if (AppManager.Instance.IsRunningCore(ECoreType.openvpn))
        {
            return "tun";
        }

        return AppManager.Instance.Config.TunModeItem.EnableTun ? "tun" : "proxy";
    }

    /// <summary>Shows a non-fatal connection error in the dashboard connect widget.</summary>
    private async Task NotifyConnectionErrorAsync(string message)
    {
        await ExecuteScriptSafelyAsync(
            $"window.setConnectionError({JsonSerializer.Serialize(message)});");
    }

    /// <summary>
    /// Bağlanma denemesi başarısız olduysa (çekirdek Failed / GPN koordinatörü
    /// Failed) dashboard'a nedeni gösteren net bir hata kartı basar. Yalnızca
    /// bağlantı kurma akışlarının sonunda çağrılır; normal disconnect no-op'tur.
    /// Öncelik GPN koordinatöründedir (seçim/launcher hatası — çekirdek hiç
    /// başlamamış olabilir), ardından ana çekirdek başlatma teşhisi gelir.
    /// </summary>
    private async Task TryPushConnectionFailureAsync()
    {
        if (!_webViewReady || ReadActualConnectionState())
        {
            return; // bağlantı kuruldu — hata kartı gösterilmez
        }

        // 1) GPN koordinatörü yakın zamanda Failed durumuna düştüyse mesajını kullan.
        var gpnFailed = _lastGpnFailedSnapshot;
        if (gpnFailed is { State: GpnConnectionState.Failed }
            && gpnFailed.Error.IsNotEmpty()
            && DateTimeOffset.UtcNow - gpnFailed.UpdatedAt < TimeSpan.FromSeconds(45))
        {
            await ShowConnectionFailureAsync(gpnFailed.Error, null, elevation: false, canRecover: false);
            return;
        }

        // 2) Ana çekirdek başlatma hatası (CoreHealthSnapshot.Error kullanıcı
        //    dostudur; teknik ayrıntı CoreStartupDiagnostic.TechnicalDetails).
        var healthFailure = _lastMainCoreFailure;
        if (healthFailure is not null
            && healthFailure.Error.IsNotEmpty()
            && DateTimeOffset.UtcNow - healthFailure.ChangedAt < TimeSpan.FromSeconds(45))
        {
            var diag = _lastMainCoreDiagnostic is { } d
                && DateTimeOffset.UtcNow - d.CreatedAt < TimeSpan.FromSeconds(45)
                ? d
                : null;
            await ShowConnectionFailureAsync(
                healthFailure.Error,
                diag?.TechnicalDetails,
                elevation: diag?.Code is CoreStartupErrorCode.ElevationRequired or CoreStartupErrorCode.ElevationFailed,
                canRecover: diag?.CanRecover == true,
                port: diag?.Port ?? healthFailure.Port);
        }
    }

    /// <summary>
    /// Bağlantı hata kartını dashboard'a basar: kullanıcı dostu mesaj + isteğe bağlı
    /// teknik ayrıntı (mono satır) + elevation hatasıysa "Relaunch as admin" butonu.
    /// </summary>
    private async Task ShowConnectionFailureAsync(
        string message,
        string? technical,
        bool elevation,
        bool canRecover,
        int? port = null)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                Message = message,
                Details = technical,
                Elevation = elevation,
                CanRecover = canRecover,
                Port = port,
            }, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setConnectionError({payload});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN connection-failure card push failed", ex);
        }
    }

    private async Task NotifySystemProxyResultAsync(bool ok, ESysProxyType type, string message)
    {
        await ExecuteScriptSafelyAsync(
            $"window.setSystemProxyResult({JsonSerializer.Serialize(new { ok, mode = (int)type, message })});");
    }

    /// <summary>
    /// Builds the toast the dashboard shows after a system-proxy change so the
    /// user always knows whether the OS proxy was actually applied — and if not,
    /// why. "Applied" carries the concrete target (127.0.0.1:port / PAC); failures
    /// surface the specific reason instead of a vague "will activate later".
    /// </summary>
    private string ComposeSystemProxyResultMessage(ESysProxyType requestedType, bool applied)
    {
        if (!applied)
        {
            // The proxy-only core failed to start (the reason is already notified)
            // — surface it instead of overriding it with a misleading message.
            if (!string.IsNullOrEmpty(_proxyOnlyService.LastFailureReason))
            {
                return $"i18n:{_proxyOnlyService.LastFailureReason}";
            }
            return "i18n:proxyOnly.pending";
        }

        if (requestedType == ESysProxyType.ForcedChange)
        {
            var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
            var target = port > 0
                ? $"{Global.Loopback}:{port}"
                : "the local proxy listener";
            return $"i18n:proxyOnly.applied|address={target}";
        }
        return requestedType == ESysProxyType.Pac
            ? "i18n:proxyOnly.appliedPac"
            : requestedType == ESysProxyType.Unchanged
                ? "i18n:proxyOnly.unchanged"
                : "i18n:proxyOnly.cleared";
    }

    /// <summary>
    /// Connects to the local SOCKS5 listener and measures round-trip time so the
    /// dashboard can confirm the proxy is reachable. Runs in under a second and
    /// does not modify any system settings.
    /// </summary>
    public async Task TestProxyAsync()
    {
        if (!_webViewReady)
        {
            return;
        }

        var port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
        if (port is < 1 or > 65535)
        {
            await ExecuteScriptSafelyAsync(
                $"window.setProxyTestResult({JsonSerializer.Serialize(new { ok = false, message = "No SOCKS5 port configured" })});");
            return;
        }

        var started = Stopwatch.StartNew();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var client = new TcpClient();
            await client.ConnectAsync(Global.Loopback, port, cts.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cts.Token);
            var response = new byte[2];
            var read = await stream.ReadAsync(response.AsMemory(0, 2), cts.Token);
            started.Stop();

            if (read == 2 && response[0] == 0x05 && response[1] == 0x00)
            {
                await ExecuteScriptSafelyAsync(
                    $"window.setProxyTestResult({JsonSerializer.Serialize(new { ok = true, ms = (int)started.ElapsedMilliseconds })});");
            }
            else
            {
                await ExecuteScriptSafelyAsync(
                    $"window.setProxyTestResult({JsonSerializer.Serialize(new { ok = false, message = "Not SOCKS5 on port " + port })});");
            }
        }
        catch (OperationCanceledException)
        {
            started.Stop();
            await ExecuteScriptSafelyAsync(
                $"window.setProxyTestResult({JsonSerializer.Serialize(new { ok = false, message = "SOCKS5 timed out on port " + port })});");
        }
        catch (Exception ex)
        {
            started.Stop();
            await ExecuteScriptSafelyAsync(
                $"window.setProxyTestResult({JsonSerializer.Serialize(new { ok = false, message = "SOCKS5 port " + port + " unreachable" })});");
            Logging.SaveLog($"AoGPN proxy test failed on port {port}", ex);
        }
    }

    private static string? _realIspIp;
    private static string? _realIspCountry;
    private static bool _ispIpCached;

    /// <summary>
    /// Son IP ölçümünün doğrulama sonucu (tunnelVerified). Bağlantı sonrası yeniden
    /// ölçüm hızı buna bağlanır: tünel henüz doğrulanmamışken (bağlanma anındaki bayat
    /// ölçüm — tünel el sıkışmayı bitirmeden alınan ISP IP'si) ~6 sn'de bir ölçülür;
    /// doğrulama kesinleşince 30 sn'ye seyrelir. Böylece yanlış "sızıntı" uyarısı
    /// saniyeler içinde düzeltilir, 30 sn asılı kalmaz.
    /// </summary>
    private static bool _lastTunnelVerified;

    /// <summary>
    /// Fetches the real public IP that the outside world sees, both with and
    /// without the tunnel, and pushes the result to the dashboard. When
    /// disconnected it shows the ISP IP; when connected via Proxy capture it
    /// compares direct vs SOCKS5-routed IP to verify the tunnel is active;
    /// when connected via TUN a single direct check suffices.
    /// </summary>
    public async Task CheckIpAsync()
    {
        if (!_webViewReady)
        {
            return;
        }

        var connected = ReadActualConnectionState();
        var transport = ReadTransport();

        try
        {
            // Ensure the ISP baseline is cached before any tunnel comparison.
            // Only (re)cache while disconnected: with TUN active every direct
            // request exits through the tunnel, so caching would record the
            // tunnel IP as the ISP baseline and break leak detection.
            if (!_ispIpCached && !connected)
            {
                await CacheIspIpAsync();
            }

            // Direct IP (no proxy) — always fetch this as the baseline.
            var directInfo = await GetIPInfoWithRetryAsync(null);
            var directIp = directInfo?.Ip ?? "";
            var directCountry = directInfo?.Country ?? "";

            // Cache the ISP IP from the first successful fetch while disconnected.
            if (!connected && directIp.Length > 0)
            {
                _realIspIp = directIp;
                _realIspCountry = directCountry;
                _ispIpCached = true;
            }

            string? tunnelIp = null;
            string? tunnelCountry = null;
            var tunnelVerified = false;

            if (connected && transport == "proxy")
            {
                // Through SOCKS5 proxy: this should give us the tunnel exit IP.
                var socksPort = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
                if (socksPort is >= 1 and <= 65535)
                {
                    var proxy = new System.Net.WebProxy($"socks5://{Global.Loopback}:{socksPort}");
                    var tunnelInfo = await GetIPInfoWithRetryAsync(proxy);
                    tunnelIp = tunnelInfo?.Ip;
                    tunnelCountry = tunnelInfo?.Country;
                }

                // Verify: tunnel IP must differ from direct IP (otherwise the
                // proxy is not actually routing traffic).
                tunnelVerified = tunnelIp is { Length: > 0 }
                    && !string.Equals(tunnelIp, directIp, StringComparison.OrdinalIgnoreCase);
            }
            else if (connected && transport == "tun")
            {
                // TUN captures all traffic; a direct check already goes through
                // the tunnel. Compare against the cached ISP IP.
                tunnelIp = directIp;
                tunnelCountry = directCountry;
                tunnelVerified = _realIspIp is { Length: > 0 }
                    && !string.Equals(directIp, _realIspIp, StringComparison.OrdinalIgnoreCase);
            }

            _lastTunnelVerified = tunnelVerified;

            // Ölçümü oturum günlüğüne de yaz — "IP değişmedi / sızıntı" belirtileri
            // canlı oturumda görülemeden gpn-session.log'dan tanımlanabilsin.
            DiagLog.Write($"GPN_IPVERIFY connected={connected} transport={transport} "
                + $"direct={directIp} tunnel={tunnelIp ?? ""} isp={_realIspIp ?? ""} "
                + $"verified={tunnelVerified} ispCached={_ispIpCached}");

            await ExecuteScriptSafelyAsync(
                $"window.setRealIpState({JsonSerializer.Serialize(new {
                    connected,
                    ispIp = _realIspIp ?? directIp,
                    ispCountry = _realIspCountry ?? directCountry,
                    directIp,
                    directCountry,
                    tunnelIp = tunnelIp ?? "",
                    tunnelCountry = tunnelCountry ?? "",
                    tunnelVerified,
                    transport,
                    ispCached = _ispIpCached,
                    measuredAt = DateTimeOffset.Now.ToString("o"),
                })});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN IP check failed", ex);
        }
    }

    /// <summary>
    /// Resolves the ISP baseline IP once at startup before any tunnel is established.
    /// </summary>
    private async Task CacheIspIpAsync()
    {
        try
        {
            var info = await GetIPInfoWithRetryAsync(null);
            if (info.HasValue && info.Value.Ip is { Length: > 0 } ip)
            {
                _realIspIp = ip;
                _realIspCountry = info.Value.Country;
                _ispIpCached = true;

                // Push the ISP baseline to the dashboard immediately so the IP card
                // shows the user's real IP even before a connection is established.
                await PushIspBaselineAsync(ip, info.Value.Country ?? "");
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN ISP IP cache failed", ex);
        }
    }

    /// <summary>
    /// Pushes the resolved ISP IP baseline to the dashboard IP card immediately.
    /// </summary>
    private async Task PushIspBaselineAsync(string ip, string country)
    {
        if (!_webViewReady || ip.Length == 0)
        {
            return;
        }

        await ExecuteScriptSafelyAsync(
            $"window.setRealIpState({System.Text.Json.JsonSerializer.Serialize(new
            {
                connected = false,
                ispIp = ip,
                ispCountry = country,
                directIp = ip,
                directCountry = country,
                tunnelIp = "",
                tunnelCountry = "",
                tunnelVerified = false,
                transport = "",
                ispCached = true,
                measuredAt = DateTimeOffset.Now.ToString("o"),
            })});");
    }

    private static async Task<IpInfoResult?> GetIPInfoWithRetryAsync(System.Net.WebProxy? proxy)
    {
        const int maxAttempts = 3;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var result = await ConnectionHandler.GetIPInfo(proxy);
                if (result.HasValue && result.Value.Ip is { Length: > 0 })
                {
                    return result;
                }
            }
            catch
            {
                // Best-effort; retry if possible
            }

            if (attempt < maxAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        return null;
    }

    /// <summary>Wave 2: <see cref="DashboardSettingsService"/> delegasyonu — gövde servise taşındı.</summary>
    private Task PushSystemProxyStateAsync(bool force = false) => _settingsService.PushSystemProxyStateAsync(force);
    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    private Task PushNodeInfoAsync(bool force = false) => _nodeService.PushNodeInfoAsync(force);

    /// <summary>
    /// Publishes the live connection table and manual-route list used by the
    /// GlassWire-style Performance and Game Boost views. The renderer receives a
    /// read-only snapshot; all mutations return through SplitTunnelViewModel so the
    /// native routing/config persistence path remains authoritative.
    /// </summary>
    internal static bool IsProtectedProcessPath(string path)
    {
        var name = ProcessCatalogService.NormalizeProcessName(path);
        return name is "aogpn.exe" or "xray.exe" or "sing-box.exe" or "mihomo.exe" or "v2ray.exe" or "openvpn.exe";
    }

    /// <summary>
    /// Runs the offline route test on a background thread and pushes the result into
    /// the dashboard. The test rebuilds the sing-box config the core would use right
    /// now and simulates rule matching — no traffic is sent and no DNS is resolved.
    /// </summary>
    public async Task RunRouteTestAsync(
        string exeName,
        string destination,
        string port,
        string network,
        string exePath)
    {
        try
        {
            var config = AppManager.Instance.Config;
            var result = await Task.Run(async () => await new RouteTesterService(config)
                .TestAsync(exeName, destination, port, network, exePath));
            var json = JsonSerializer.Serialize(result, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.showRouteTestResult({json});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN route test failed", ex);
        }
    }

    /// <summary>Wave 3: <see cref="DashboardPushService"/> delegasyonu — gövde servise taşındı.</summary>
    private Task PushGpnDiagAsync(GpnDiagEvent evt) => _pushService.PushGpnDiagAsync(evt);
    /// <summary>Wave 3: <see cref="DashboardPushService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task PushGpnTelemetryAsync() => _pushService.PushGpnTelemetryAsync();
    /// <summary>Wave 3: <see cref="DashboardPushService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task PushGpnCaptureStatsAsync() => _pushService.PushGpnCaptureStatsAsync();
    /// <summary>Wave 3: <see cref="DashboardPushService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task PushGpnPidPoolAsync() => _pushService.PushGpnPidPoolAsync();
    /// <summary>Wave 3: <see cref="DashboardPushService"/> delegasyonu — gövde servise taşındı.</summary>
    private Task PushWarpHealthAsync() => _pushService.PushWarpHealthAsync();
    /// <summary>Wave 3: <see cref="DashboardPushService"/> delegasyonu — gövde servise taşındı.</summary>
    private Task PushWinDivertHealthAsync() => _pushService.PushWinDivertHealthAsync();
    /// <summary>Wave 3: <see cref="DashboardPushService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task PushGpnResilienceLogAsync() => _pushService.PushGpnResilienceLogAsync();
    /// <summary>Wave 3: <see cref="DashboardPushService"/> delegasyonu — gövde servise taşındı.</summary>
    private Task PushGpnDrainAsync(GpnDrainSnapshot snap) => _pushService.PushGpnDrainAsync(snap);
    /// <summary>Wave 3: <see cref="DashboardPushService"/> delegasyonu — gövde servise taşındı.</summary>
    private Task PushGpnResilienceAsync(GpnResilienceEvent evt) => _pushService.PushGpnResilienceAsync(evt);
    /// <summary>Wave 3: <see cref="DashboardPushService"/> delegasyonu — gövde servise taşındı.</summary>
    private Task PushAvailabilityInfoAsync(AvailabilityCheckResult result) => _pushService.PushAvailabilityInfoAsync(result);

    // ── GPN Sunucu Yönetimi (Faz 3 ekranı) ───────────────────────────────

    /// <summary>Wave 2: <see cref="DashboardGpnServerService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task PushGpnServersAsync() => _gpnServerService.PushGpnServersAsync();
    /// <summary>Wave 2: <see cref="DashboardGpnServerService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task ProbeGpnServersAsync() => _gpnServerService.ProbeGpnServersAsync();
    /// <summary>Wave 2: <see cref="DashboardGpnServerService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task RestoreGpnDefaultsAsync() => _gpnServerService.RestoreGpnDefaultsAsync();
    /// <summary>Wave 2: <see cref="DashboardGpnServerService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task PushGpnDefaultsStatusAsync(WireGuardServerCatalog.GpnDefaultsRestoreResult? restoreResult = null) => _gpnServerService.PushGpnDefaultsStatusAsync(restoreResult);
    /// <summary>Wave 2: <see cref="DashboardGpnServerService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task ImportGpnServersAsync(string confText) => _gpnServerService.ImportGpnServersAsync(confText);
    /// <summary>Wave 2: <see cref="DashboardGpnServerService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task DeleteGpnServerAsync(string serverId) => _gpnServerService.DeleteGpnServerAsync(serverId);
    /// <summary>Wave 2: <see cref="DashboardGpnServerService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task ToggleGpnServerAsync(string serverId, bool enabled) => _gpnServerService.ToggleGpnServerAsync(serverId, enabled);
    /// <summary>Wave 2: <see cref="DashboardGpnServerService"/> delegasyonu — gövde servise taşındı.</summary>
    private Task NotifyGpnServersOpAsync(string message) => _gpnServerService.NotifyGpnServersOpAsync(message);
    /// <summary>
    /// Sunucu Yönetimi ekranından WPF ekleme/düzenleme penceresini açar.
    /// <paramref name="existingServerId"/> verilirse o satır düzenlenir (özel anahtar
    /// DPAPI'den çözülür, alan boş bırakılırsa mevcut anahtar korunur). Kayıt başarılıysa
    /// katalog zaten güncellenmiştir; dashboard listesi tazelenir.
    /// </summary>
    public async Task ShowGpnServerEditDialogAsync(string? existingServerId)
    {
        GpnServerEditViewModel? viewModel = null;
        if (existingServerId.IsNotEmpty())
        {
            var items = await WireGuardServerCatalog.GetItemsAsync();
            var row = (items ?? []).FirstOrDefault(i => i.ServerId == existingServerId);
            if (row is not null && WireGuardServerCatalog.TryMap(row, out var profile))
            {
                viewModel = new GpnServerEditViewModel(profile);
            }
        }
        viewModel ??= new GpnServerEditViewModel();

        if (await AppManager.Instance.WindowDialog.ShowDialogAsync(viewModel) == true)
        {
            await PushGpnServersAsync();
            await NotifyGpnServersOpAsync(viewModel.SavedProfile is not null
                ? $"GPN sunucusu kaydedildi: {viewModel.SavedProfile.Name}"
                : "GPN sunucusu kaydedildi.");
        }
    }

    /// <summary>Wave 3: <see cref="DashboardPushService"/> delegasyonu — gövde servise taşındı.</summary>
    private Task PushRuleDriftAsync(bool force = false) => _pushService.PushRuleDriftAsync(force);
    /// <summary>Wave 3: <see cref="DashboardPushService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task PushProcessCatalogAsync() => _pushService.PushProcessCatalogAsync();
    /// <summary>Wave 3: <see cref="DashboardPushService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task PushMonitorSnapshotAsync(bool force = false) => _pushService.PushMonitorSnapshotAsync(force);

    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task SelectNodeAsync(string indexId) => _nodeService.SelectNodeAsync(indexId);

    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task CopyNodesAsync(string[] indexIds) => _nodeService.CopyNodesAsync(indexIds);
    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task PasteNodesAsync() => _nodeService.PasteNodesAsync();
    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task DeleteNodesAsync(string[] indexIds) => _nodeService.DeleteNodesAsync(indexIds);

    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task StartNodeSpeedtestAsync(string[] indexIds, string? testType = null, long requestedRunId = 0) => _nodeService.StartNodeSpeedtestAsync(indexIds, testType, requestedRunId);
    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public void StopNodeSpeedtest() => _nodeService.StopNodeSpeedtest();

    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task DisableNodesAsync(string[] indexIds) => _nodeService.DisableNodesAsync(indexIds);
    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task RestoreNodesAsync(string[] indexIds) => _nodeService.RestoreNodesAsync(indexIds);
    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task CleanupFailedNodesAsync(string target) => _nodeService.CleanupFailedNodesAsync(target);
    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task DedupNodesAsync() => _nodeService.DedupNodesAsync();

    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task PushNodePoolAsync() => _nodeService.PushNodePoolAsync();
    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task AddNodePoolLinkAsync(string url) => _nodeService.AddNodePoolLinkAsync(url);
    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task EditNodePoolLinkAsync(string url, string newUrl) => _nodeService.EditNodePoolLinkAsync(url, newUrl);
    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task RemoveNodePoolLinkAsync(string url) => _nodeService.RemoveNodePoolLinkAsync(url);
    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task FetchNodePoolAsync() => _nodeService.FetchNodePoolAsync();
    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task ToggleNodeFavAsync(string indexId) => _nodeService.ToggleNodeFavAsync(indexId);

    /// <summary>Shows a transient toast in the dashboard's Nodes view.</summary>
    public async Task NotifyNodesOpAsync(string message)
    {
        if (!_webViewReady)
        {
            return;
        }
        await ExecuteScriptSafelyAsync($"window.notifyNodes({JsonSerializer.Serialize(message)});");
    }

    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    private Task PushNodeListAsync() => _nodeService.PushNodeListAsync();

    /// <summary>Wave 2: <see cref="DashboardSettingsService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task PushSettingsAsync() => _settingsService.PushSettingsAsync();
    /// <summary>Wave 2: <see cref="DashboardSettingsService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task SaveSettingsAsync(JsonElement root) => _settingsService.SaveSettingsAsync(root);
    /// <summary>
    /// Polls the effective routing/core state away from the UI thread. Real ping and
    /// packet-loss samples come from TelemetryDashboardViewModel, not this loop.
    /// </summary>
    private void StartTelemetryLoop()
    {
        if (_connectionLifecycleTask is not null)
        {
            return;
        }

        _connectionLifecycleTask = Task.Run(
            () => ConnectionLifecycleLoopAsync(_webViewLifetime.Token),
            _webViewLifetime.Token);
    }

    private async Task ConnectionLifecycleLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        var ipCheckCounter = 0;
        var driftCheckCounter = 0;
        var lastPublishedConnectionState = false;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // Polling also catches mode changes made by the game-trigger/native
                // flows, which do not originate in the WebView2 message bridge.
                var stateOperation = Dispatcher.InvokeAsync(
                    () => SynchronizeConnectionStateAsync(),
                    DispatcherPriority.Background);
                await stateOperation.Task.Unwrap().ConfigureAwait(false);

                // Stale-rule health check: run immediately when the tunnel comes up
                // (so a connect that skipped the reload cannot hide drift) and then
                // every ~30 s (15 × 2 s ticks) while the app is alive, so rules edited
                // in the routing settings without a reload are surfaced before they
                // cause failures.
                var connectedNow = Volatile.Read(ref _connectionState);
                if (connectedNow != lastPublishedConnectionState || ++driftCheckCounter >= 15)
                {
                    driftCheckCounter = 0;
                    var driftOperation = Dispatcher.InvokeAsync(
                        () => PushRuleDriftAsync(),
                        DispatcherPriority.Background);
                    await driftOperation.Task.Unwrap().ConfigureAwait(false);
                }

                // Bağlantı anı: tünel daha hazır değilken alınmış bayat IP ölçümünü
                // ("sızıntı" yanlış uyarısı) bir sonraki tick'te (≈2 sn) yeniden ölç.
                if (connectedNow && !lastPublishedConnectionState)
                {
                    ipCheckCounter = 3; // hızlı aralığı tetikle
                    var ipNowOperation = Dispatcher.InvokeAsync(
                        () => CheckIpAsync(),
                        DispatcherPriority.Background);
                    await ipNowOperation.Task.Unwrap().ConfigureAwait(false);
                }
                lastPublishedConnectionState = connectedNow;

                // REALITY nodes that fail on the sing-box core (its hardcoded 1.8.1
                // handshake claim is rejected by modern 3x-ui servers) fall back to
                // the Xray core automatically when TUN is off, or get an explanatory
                // notice when TUN blocks the switch.
                var fallbackOperation = Dispatcher.InvokeAsync(
                    () => SuggestRealityCoreFallbackAsync(),
                    DispatcherPriority.Background);
                await fallbackOperation.Task.Unwrap().ConfigureAwait(false);

                // Keep the tray status line (connection / proxy-only / idle) live even
                // when the window is hidden to the tray.
                var trayStatusOperation = Dispatcher.InvokeAsync(
                    () => UpdateTrayStatus(),
                    DispatcherPriority.Background);
                await trayStatusOperation.Task.ConfigureAwait(false);

                // Surface server switches and proxy changes made outside the WebView2
                // bridge (native lists, tray flows, hotkeys) while the app is alive.
                var proxyOperation = Dispatcher.InvokeAsync(
                    () => PushSystemProxyStateAsync(),
                    DispatcherPriority.Background);
                await proxyOperation.Task.Unwrap().ConfigureAwait(false);

                var monitorOperation = Dispatcher.InvokeAsync(
                    () => PushMonitorSnapshotAsync(),
                    DispatcherPriority.Background);
                await monitorOperation.Task.Unwrap().ConfigureAwait(false);

                if (Volatile.Read(ref _connectionState))
                {
                    var nodeOperation = Dispatcher.InvokeAsync(
                        () => PushNodeInfoAsync(),
                        DispatcherPriority.Background);
                    await nodeOperation.Task.Unwrap().ConfigureAwait(false);

                    // IP panelini periyodik YENİDEN ölç: doğrulanana dek hızlı
                    // (3 tick ≈ 6 sn — bağlanma anındaki bayat ölçümün "sızıntı"
                    // uyarısı saniyeler içinde düzeltilir), doğrulama kesinleşince
                    // 30 sn'ye seyrel. Orta oturum sızıntılarını da yakalar: çöken
                    // TUN sürücüsü, sessizce yeniden başlayan proxy, core çıkışı.
                    ipCheckCounter++;
                    var ipFastTicks = _lastTunnelVerified ? 15 : 3;
                    if (ipCheckCounter >= ipFastTicks)
                    {
                        ipCheckCounter = 0;
                        var ipCheckOperation = Dispatcher.InvokeAsync(
                            () => CheckIpAsync(),
                            DispatcherPriority.Background);
                        await ipCheckOperation.Task.Unwrap().ConfigureAwait(false);
                    }
                }
                else
                {
                    ipCheckCounter = 0;
                }

                // Keep the title bar maximize/restore icon in sync with the real
                // window state (Win+Up/Down, snap, drag-to-top maximize).
                var windowStateOperation = Dispatcher.InvokeAsync(
                    () => SynchronizeWindowStateAsync(),
                    DispatcherPriority.Background);
                await windowStateOperation.Task.Unwrap().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown path.
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN connection lifecycle poll stopped unexpectedly", ex);
        }
    }

    /// <summary>
    /// Activates the existing AoGPN telemetry sampler and forwards its real samples to
    /// the WebView2 DOM. The sampler measures through the local SOCKS proxy and derives
    /// loss from successful versus timed-out probes.
    /// </summary>
    private void StartTelemetryBridge()
    {
        var telemetry = ViewModel?.ConnectionViewModel?.Telemetry;
        if (telemetry is null || ReferenceEquals(_telemetryDashboard, telemetry))
        {
            return;
        }

        _telemetryDashboard = telemetry;
        telemetry.SamplesChanged += TelemetryDashboard_SamplesChanged;
        telemetry.SetActive(true);
    }

    private async void TelemetryDashboard_SamplesChanged()
    {
        if (_isClosing)
        {
            return;
        }

        try
        {
            if (!Dispatcher.CheckAccess())
            {
                var operation = Dispatcher.InvokeAsync(
                    () => PushRealTelemetryAsync(),
                    DispatcherPriority.Background);
                await operation.Task.Unwrap();
                return;
            }

            await PushRealTelemetryAsync();
        }
        catch (Exception ex)
        {
            if (!_isClosing)
            {
                Logging.SaveLog("AoGPN real telemetry bridge failed", ex);
            }
        }
    }

    private async Task PushRealTelemetryAsync()
    {
        var telemetry = _telemetryDashboard;
        if (telemetry is null || !_webViewReady || !ReadActualConnectionState())
        {
            return;
        }

        // Null keeps the frontend in its "measuring" state until a real probe result
        // exists; it never falls back to a random value in WebView2.
        var ping = telemetry.PingValue > 0 ? telemetry.PingValue : (int?)null;
        var packetLoss = telemetry.LossValue >= 0 ? telemetry.LossValue : (double?)null;
        await PushTelemetryAsync(ping, packetLoss, telemetry.DownValue, telemetry.UpValue);
    }

    private async Task PushTelemetryAsync(
        int? ping,
        double? packetLoss,
        double downloadMbps,
        double uploadMbps)
    {
        var pingJson = JsonSerializer.Serialize(ping);
        var packetLossJson = JsonSerializer.Serialize(packetLoss);
        var downloadJson = JsonSerializer.Serialize(Math.Max(0, downloadMbps));
        var uploadJson = JsonSerializer.Serialize(Math.Max(0, uploadMbps));
        await ExecuteScriptSafelyAsync(
            $"window.updateTelemetry({pingJson}, {packetLossJson}, {downloadJson}, {uploadJson});");
    }

    private void StopTelemetryBridge()
    {
        if (_telemetryDashboard is null)
        {
            return;
        }

        _telemetryDashboard.SamplesChanged -= TelemetryDashboard_SamplesChanged;
        _telemetryDashboard.SetActive(false);
        _telemetryDashboard = null;
    }

    private async Task ExecuteScriptSafelyAsync(string script)
    {
        if (_isClosing)
        {
            return;
        }

        // WebView2 is a WPF control: its CoreWebView2 must only be touched from the
        // UI thread. Background callers (e.g. the ISP IP cache primed via Task.Run)
        // are marshaled here so they cannot throw cross-thread InvalidOperationException.
        if (!Dispatcher.CheckAccess())
        {
            var operation = Dispatcher.InvokeAsync(
                () => ExecuteScriptSafelyAsync(script),
                DispatcherPriority.Background);
            await operation.Task.Unwrap();
            return;
        }

        if (!_webViewReady || WebView.CoreWebView2 is null)
        {
            return;
        }

        if (WebView.CoreWebView2.IsSuspended)
        {
            // The dashboard is frozen (window minimized to the tray); nothing to
            // push until Resume() runs, and script execution is not allowed
            // while suspended.
            return;
        }

        try
        {
            await _dashboardHost.ExecuteScriptAsync(script);
        }
        catch (Exception ex) when (
            ex is COMException
            or InvalidOperationException
            or ObjectDisposedException)
        {
            // WebView2 can be torn down concurrently with a timer tick during close.
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _isClosing = true;
        StopNodeSpeedtest();
        StopTelemetryBridge();
        _pushService.DisposePidBridge();
        _warpAutoRecover?.Dispose();

        // Remove the shell tray icon on EVERY exit path, not just the tray menu's
        // Exit item. Exits through the HTML close button, AppExitAsync, shutdown or
        // session ending used to leave the icon registered, so each restart stacked
        // a dead "ghost" icon in the notification area and the user could end up
        // clicking an icon that belongs to a previous process — clicks went nowhere.
        try
        {
            if (contentStatusBarView.Content is StatusBarView sbv && sbv.tbNotify is { } tb)
            {
                tb.Dispose();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("Tray icon dispose on close failed", ex);
        }

        _webViewLifetime.Cancel();

        _dashboardHost.WebMessageReceived -= _dashboardMessageDispatcher.HandleWebMessageReceived;
        _dashboardHost.NavigationCompleted -= CoreWebView2_NavigationCompleted;
        _dashboardHost.DisposeAsync().GetAwaiter().GetResult();
        _webViewLifetime.Dispose();
    }

    private const int WmNcLButtonDown = 0x00A1;
    private static readonly IntPtr HtCaption = new(2);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SendMessage(
        IntPtr hWnd,
        int message,
        IntPtr wParam,
        IntPtr lParam);

    private const int GwlStyle = -16;
    private const int WsThickFrame = 0x00040000;
    private const int WsMaximizeBox = 0x00010000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, int dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCapture(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint ptReserved;
        public NativePoint ptMaxSize;
        public NativePoint ptMaxPosition;
        public NativePoint ptMinTrackSize;
        public NativePoint ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public int dwFlags;
    }

    #endregion WebView2 bridge

    #region Event

    private void OnProgramStarted(object state, bool timeout)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            // Bring the existing window to front when a second instance starts.
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Topmost = true;
            Activate();
            Focus();
            Topmost = false;

            // Show a tray notification so the user knows the app was already running.
            if (contentStatusBarView.Content is StatusBarView sbv && sbv.tbNotify != null)
            {
                sbv.tbNotify.ShowNotification("AoGPN is already running", "A second instance was blocked. The existing window has been brought to the front.");
            }
            ShowHideWindow(true);

            // Re-register the wait so further instances also trigger this handler.
            ThreadPool.RegisterWaitForSingleObject(App.ProgramStarted, OnProgramStarted, null, -1, false);
        });
    }

    private async Task DelegateSnackMsg(string content)
    {
        MainSnackbar.MessageQueue?.Enqueue(content);
        await Task.CompletedTask;
    }

    private async Task DelegateSnackAction(ActionNotice notice)
    {
        MainSnackbar.MessageQueue?.Enqueue(
            notice.Content, notice.ActionContent, notice.Handler);
        await Task.CompletedTask;
    }

    private void OnHotkeyHandler(EGlobalHotkey e)
    {
        switch (e)
        {
            case EGlobalHotkey.ShowForm:
                ShowHideWindow(null);
                break;

            case EGlobalHotkey.SystemProxyClear:
            case EGlobalHotkey.SystemProxySet:
            case EGlobalHotkey.SystemProxyUnchanged:
            case EGlobalHotkey.SystemProxyPac:
                AppEvents.SysProxyChangeRequested.Publish((ESysProxyType)((int)e - 1));
                break;
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        // Preserve the tray behavior for Alt+F4/system close, while allowing the
        // explicit HTML close button to terminate the application normally.
        if (_allowClose)
        {
            return;
        }

        // Only hide to the tray when the user opted in; otherwise closing means a
        // real application exit. The coordinator owns the hide-only vs exit split so
        // the tray-hide invariant stays guarded in one place.
        switch (_windowLifecycle.DecideClose(_config, _allowClose))
        {
            case WindowLifecycleAction.AllowClose:
                return;
            case WindowLifecycleAction.Exit:
                _allowClose = true;
                // Oturum denetim günlüklerini kapat (test süreci diske tam yazılmış
                // olsun — VPN bağlantısı kopsa da gpn-session.log / vpn-session.log
                // okunabilir kalır). Aktif olmayan taraf no-op'tur.
                GpnSessionLog.EndSession("app exit");
                VpnSessionLog.EndSession("app exit");
                _ = ExitApplicationSafelyAsync();
                return;
            case WindowLifecycleAction.HideToTray:
                e.Cancel = true;
                _trayBehavior.CloseToTray();
                return;
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        // Minimize-to-tray: delegate the decision (and the one-time tray hint) to the
        // coordinator, which only hides the window and never touches the core or the
        // OS proxy (see TrayWindowCoordinator).
        //
        // The startup minimize from AutoHideStartup is ignored while _startupTrayPending
        // is set: OnLoaded owns that transition (tray icon first, then hide), so the
        // window cannot disappear before the tray icon exists.
        var action = _windowLifecycle.DecideStateChange(
            _config,
            WindowState == WindowState.Minimized,
            _startupTrayPending);
        if (action == WindowLifecycleAction.HideToTray)
        {
            _trayBehavior.HandleMinimize(_config);
        }

        // The dashboard must not keep compositing while the window is hidden in
        // the tray (or simply minimized): TrySuspendAsync freezes the browser
        // processes entirely instead of letting its rAF/CSS effects hold the
        // WebView2 GPU process busy forever. Resume() restores it on show.
        _ = SyncWebViewSuspensionAsync(WindowState == WindowState.Minimized);
    }

    /// <summary>
    /// Freezes / resumes the WebView2 dashboard while the window is hidden (the
    /// window is kept alive minimized, never Window.Hide()d, so the tray icon
    /// stays registered). A hidden dashboard would otherwise keep compositing its
    /// CSS/requestAnimationFrame effects and hold the WebView2 GPU process near
    /// 100% indefinitely; TrySuspendAsync stops the browser processes entirely.
    /// </summary>
    private async Task SyncWebViewSuspensionAsync(bool shouldSuspend)
    {
        if (_isClosing || !_webViewReady || WebView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            if (shouldSuspend)
            {
                if (!WebView.CoreWebView2.IsSuspended)
                {
                    await WebView.CoreWebView2.TrySuspendAsync();
                }
            }
            else if (WebView.CoreWebView2.IsSuspended)
            {
                WebView.CoreWebView2.Resume();
            }
        }
        catch (Exception ex) when (
            ex is COMException
            or InvalidOperationException
            or ObjectDisposedException)
        {
            // WebView2 can be torn down concurrently with a hide/show during close.
        }
    }

    private void ShowTrayMinimizeHint()
    {
        // Show the tray hint exactly once per session so the user knows the app
        // (and any proxy-only core) keeps running after the window disappears.
        // Best-effort: H.NotifyIcon throws "TrayIcon is not created" when the
        // native shell icon is not ready yet (e.g. right after a hidden startup),
        // and that must never surface as an unhandled dispatcher exception.
        try
        {
            if (contentStatusBarView.Content is StatusBarView sbv && sbv.tbNotify != null)
            {
                sbv.tbNotify.ShowNotification(ResUI.TrayMinimizedHintTitle, ResUI.TrayMinimizedHintContent);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("Tray minimize hint failed", ex);
        }
    }

    /// <summary>Runs the tray exit path, swallowing failures the same way AppExitAsync did.</summary>
    private async Task ExitApplicationSafelyAsync()
    {
        try
        {
            await _trayBehavior.ExitApplicationAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN window exit failed", ex);
        }
    }

    private async void Current_SessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        Logging.SaveLog("Current_SessionEnding");
        StorageUI();
        await AppManager.Instance.AppExitAsync(false);
    }

    private void Shutdown(bool obj)
    {
        // Any request that reaches Application.Shutdown is a real exit (tray Exit,
        // HTML close, reboot-as-admin, restore flow). Allow the window to close so
        // MainWindow_Closing cannot swallow it into a tray-hide, which would leave
        // a hidden process with the core stopped, the proxy cleared and no tray icon.
        _allowClose = true;
        Application.Current.Shutdown();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
        {
            switch (e.Key)
            {
                case Key.V:
                    // The dashboard owns the paste gesture now: the Nodes view
                    // handles Ctrl+V itself and posts paste_nodes to the bridge.
                    // While WebView2 is loaded it is the only visible surface, so
                    // run the legacy import only when it is not (IsKeyboardFocused
                    // is unreliable here: the browser is a child HWND whose focus
                    // WPF does not track).
                    if (Keyboard.FocusedElement is TextBox || WebView.CoreWebView2 is not null)
                    {
                        return;
                    }
                    AddServerViaClipboardAsync().ContinueWith(_ => { });

                    break;

                case Key.S:
                    ScanScreenTaskAsync().ContinueWith(_ => { });
                    break;
            }
        }
        else
        {
            if (e.Key == Key.F5)
            {
                ViewModel?.Reload();
            }
        }
    }

    private void MenuClose_Click(object sender, RoutedEventArgs e)
    {
        StorageUI();
        _trayBehavior.CloseToTray();
    }

    private void MenuPromotion_Click(object sender, RoutedEventArgs e)
    {
        ProcUtils.ProcessStart($"{Utils.Base64Decode(Global.PromotionUrl)}?t={DateTime.Now.Ticks}");
    }

    private void menuOpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var logPath = Utils.GetLogPath();
        try
        {
            ProcUtils.ProcessStart(logPath);
        }
        catch
        {
            ProcUtils.ProcessStart(Utils.GetBinConfigPath());
        }
    }

    private void menuVerboseLogging_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem item)
        {
            _config.GuiItem.EnableVerboseLog = item.IsChecked;
            _config.GuiItem.EnableLog = true;
            Logging.VerboseLoggingEnabled(item.IsChecked);
            Logging.LoggingEnabled(true);
            Logging.Verbose("UI", "verbose_toggle", ("enabled", item.IsChecked));
        }
    }

    private void MenuSettingsSetUWP_Click(object sender, RoutedEventArgs e)
    {
        ProcUtils.ProcessStart(Utils.GetBinPath("EnableLoopback.exe"));
    }

    public async Task AddServerViaClipboardAsync()
    {
        var clipboardData = WindowsUtils.GetClipboardData();
        if (clipboardData.IsNotEmpty() && ViewModel != null)
        {
            await ViewModel.AddServerViaClipboardAsync(clipboardData);
        }
    }

    private async Task ScanScreenTaskAsync()
    {
        ShowHideWindow(false);

        if (Application.Current?.MainWindow is Window window)
        {
            var bytes = QRCodeWindowsUtils.CaptureScreen(window);
            await ViewModel?.ScanScreenResult(bytes);
        }

        ShowHideWindow(true);
    }

    private void MenuCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        _checkUpdateView ??= new CheckUpdateView();
        _checkUpdateView.ViewModel = ViewModel?.CheckUpdateViewModel;
        DialogHost.Show(_checkUpdateView, "RootDialog");

        AppEvents.HasUpdateNotified.Publish(false);
    }

    private void MenuBackupAndRestore_Click(object sender, RoutedEventArgs e)
    {
        _backupAndRestoreView ??= new BackupAndRestoreView();
        _backupAndRestoreView.ViewModel = ViewModel?.BackupAndRestoreViewModel;
        DialogHost.Show(_backupAndRestoreView, "RootDialog");
    }

    #endregion Event

    #region UI

    private void SetActiveNav(Button active)
    {
        foreach (var button in new[]
        {
            btnNavServers, btnNavMsg, btnNavAddServer, btnNavImport, btnNavScan,
            btnNavConnection, btnNavProxies, btnNavConnections, btnNavGameBoost,
            btnNavSettings, btnNavRouting, btnNavDNS
        })
        {
            button.ClearValue(BackgroundProperty);
            button.ClearValue(ForegroundProperty);
            NavState.SetIsActive(button, false);
        }

        // The SidebarNavButton template renders the active gradient wash + accent bar.
        NavState.SetIsActive(active, true);
    }

    private void SyncActiveNav(int index)
    {
        var button = index switch
        {
            0 => btnNavServers,
            1 => btnNavMsg,
            2 => btnNavProxies,
            3 => btnNavConnections,
            4 => btnNavConnection,
            _ => null,
        };

        if (button is not null)
        {
            SetActiveNav(button);
        }
    }

    public void ShowHideWindow(bool? blShow)
    {
        // Toggle requests (tray left click, ShowForm hotkey) decide from the window's
        // LIVE state, not the cached flag: the flag desyncs when the window is
        // minimized without hiding to the tray (Minimize2Tray off) — the tray click
        // must then restore the window, never hide it again ("clicking the tray icon
        // does not bring the window back"). See TrayWindowCoordinator.ShouldShowOnToggle.
        var bl = blShow ?? _windowLifecycle.ShouldShowOnToggle(ShowInTaskbar, WindowState == WindowState.Minimized);
        if (bl)
        {
            ShowInTaskbar = true;
            this?.Show();
            if (this?.WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            this?.Activate();
            this?.Focus();
        }
        else
        {
            // Hide to the tray by trapping the window minimized with no taskbar button
            // instead of calling Window.Hide(). Keeping the window (and its visual tree)
            // alive guarantees the hosting TaskbarIcon stays registered in the system tray;
            // Window.Hide() in H.NotifyIcon.Wpf can tear down that registration, which is
            // the regression behind "minimizing makes the app vanish from the tray".
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
        }
        AppManager.Instance.ShowInTaskbar = bl;
    }

    protected override void OnLoaded(object? sender, RoutedEventArgs e)
    {
        base.OnLoaded(sender, e);
        if (!_config.UiItem.AutoHideStartup)
        {
            return;
        }

        // Complete the hidden startup here, at Loaded dispatcher priority, so the
        // status bar (tray icon host) is already in the visual tree. Hiding first
        // would leave the app with no window, no taskbar button AND no tray icon —
        // a running process the user cannot reach, because H.NotifyIcon cannot
        // register the shell icon while the window is minimized. The tray icon is
        // therefore created (ForceCreate) before the window is hidden.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_config.UiItem.Minimize2Tray)
            {
                TryCreateTrayIcon();
                ShowHideWindow(false);
            }
            // AutoHideStartup without minimize-to-tray: stay minimized in the
            // taskbar (the constructor already minimized the window).
            _startupTrayPending = false;
        });
    }

    /// <summary>
    /// Ensures the status bar's TaskbarIcon has registered its native shell icon.
    /// Safe to call repeatedly; a missing or not-yet-loaded status bar is skipped.
    /// </summary>
    private void TryCreateTrayIcon()
    {
        try
        {
            if (contentStatusBarView.Content is StatusBarView sbv
                && sbv.tbNotify is { IsCreated: false })
            {
                sbv.tbNotify.ForceCreate();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("Tray icon creation for hidden startup failed", ex);
        }
    }

    private void StorageUI()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        ConfigHandler.SaveWindowSizeItem(_config, GetType().Name,
            isMaximized ? RestoreBounds.Width : Width,
            isMaximized ? RestoreBounds.Height : Height,
            isMaximized,
            isMaximized ? RestoreBounds.Left : Left,
            isMaximized ? RestoreBounds.Top : Top);

        if (colSidebar.Width.IsAbsolute)
        {
            _config.UiItem.SidebarWidth = (int)colSidebar.Width.Value;
        }

    }

    private void BindSidebarConnectionMode(StatusBarViewModel vm)
    {
        if (vm == null)
        {
            return;
        }

        // Bind directly against the singleton StatusBarViewModel. ReactiveUI's
        // this.Bind(...) overloads would cast this.ViewModel (MainWindowViewModel)
        // to StatusBarViewModel because MainWindow is IViewFor<MainWindowViewModel>,
        // which throws at startup. Plain WPF two-way sync keeps the sidebar controls
        // driving the exact same properties the header combo/toggle already use.
        cmbSideSystemProxy.SelectedIndex = vm.SystemProxySelected;
        togSideTun.IsChecked = vm.EnableTun;

        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(StatusBarViewModel.SystemProxySelected))
            {
                cmbSideSystemProxy.SelectedIndex = vm.SystemProxySelected;
            }
            else if (args.PropertyName == nameof(StatusBarViewModel.EnableTun))
            {
                togSideTun.IsChecked = vm.EnableTun;
            }
        };

        cmbSideSystemProxy.SelectionChanged += (_, _) =>
        {
            if (cmbSideSystemProxy.SelectedIndex >= 0)
            {
                vm.SystemProxySelected = cmbSideSystemProxy.SelectedIndex;
            }
        };

        togSideTun.Checked += (_, _) => vm.EnableTun = true;
        togSideTun.Unchecked += (_, _) => vm.EnableTun = false;
    }

    private void InitLayoutBindings()
    {
        var currentLayoutDisposables = new CompositeDisposable();
        _layoutBindingsDisposable.Disposable = currentLayoutDisposables;

        // AoGPN: single-pane layout. The split server/dashboard views are gone;
        // servers, logs, proxies, connections and monitor live in the left rail tabs.
        gridMain.Visibility = Visibility.Collapsed;
        gridMain1.Visibility = Visibility.Collapsed;
        gridMain2.Visibility = Visibility.Visible;

        this.WhenAnyValue(v => v.ViewModel.ProfilesViewModel)
            .Subscribe(vm => ViewHost.Show(tabProfiles2, vm))
            .DisposeWith(currentLayoutDisposables);
        this.WhenAnyValue(v => v.ViewModel.MsgViewModel)
            .Subscribe(vm => ViewHost.Show(tabMsgView2, vm))
            .DisposeWith(currentLayoutDisposables);
        this.WhenAnyValue(v => v.ViewModel.ClashProxiesViewModel)
            .Subscribe(vm => ViewHost.Show(tabClashProxies2, vm))
            .DisposeWith(currentLayoutDisposables);
        this.WhenAnyValue(v => v.ViewModel.ClashConnectionsViewModel)
            .Subscribe(vm => ViewHost.Show(tabClashConnections2, vm))
            .DisposeWith(currentLayoutDisposables);
        this.WhenAnyValue(v => v.ViewModel.ConnectionViewModel)
            .Subscribe(vm => ViewHost.Show(tabConnection2, vm))
            .DisposeWith(currentLayoutDisposables);
        this.OneWayBind(ViewModel, vm => vm.ShowClashUI, v => v.tabClashProxies2.Visibility).DisposeWith(currentLayoutDisposables);
        this.OneWayBind(ViewModel, vm => vm.ShowClashUI, v => v.tabClashConnections2.Visibility).DisposeWith(currentLayoutDisposables);
        this.Bind(ViewModel, vm => vm.TabMainSelectedIndex, v => v.tabMain2.SelectedIndex).DisposeWith(currentLayoutDisposables);

        // Land on the unified connection dashboard (mode + monitoring + app routing).
        ViewModel.TabMainSelectedIndex = 4;
    }

    private void AddHelpMenuItem()
    {
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo();
        foreach (var it in coreInfo
            .Where(t => t.CoreType is not ECoreType.v2fly
                        and not ECoreType.hysteria))
        {
            var item = new MenuItem()
            {
                Tag = it.Url.Replace(@"/releases", ""),
                Header = string.Format(ResUI.menuWebsiteItem, it.CoreType.ToString().Replace("_", " ")).UpperFirstChar()
            };
            item.Click += MenuItem_Click;
            menuHelp.Items.Add(item);
        }
    }

    private void MenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item)
        {
            ProcUtils.ProcessStart(item.Tag.ToString());
        }
    }

    #endregion UI

    #region IDashboardBridge (window-owned operations exposed to the dashboard dispatcher)

    bool IDashboardBridge.IsClosing => _isClosing;

    string IDashboardBridge.ActiveView
    {
        get => _activeView;
        set => _activeView = value;
    }

    long IDashboardBridge.NodeTestRunId => _nodeService.NodeTestRunId;

    GpnTargetResolverBridge IDashboardBridge.GpnPidBridge => _pushService.GpnPidBridge;

    void IDashboardBridge.ResetGpnTelemetry() => _pushService.ResetGpnTelemetry();

    void IDashboardBridge.ClearGpnResilienceLog() => _pushService.ClearGpnResilienceLog();

    bool IDashboardBridge.TryResolveExecutablePath(int pid, out string runningPath)
        => _pushService.TryResolveExecutablePath(pid, out runningPath);

    bool IDashboardBridge.TryApplySidebarTheme(string wpfTheme)
    {
        if (_sidebarThemeVm is null)
        {
            return false;
        }

        _sidebarThemeVm.CurrentTheme = wpfTheme;
        cmbSidebarTheme.SelectedValue = wpfTheme;
        return true;
    }

    #endregion IDashboardBridge
}
