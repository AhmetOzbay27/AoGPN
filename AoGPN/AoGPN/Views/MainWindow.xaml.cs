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

public partial class MainWindow
{
    private static Config _config;
    private readonly SerialDisposable _layoutBindingsDisposable = new();
    private CheckUpdateView? _checkUpdateView;
    private BackupAndRestoreView? _backupAndRestoreView;
    private ThemeSettingViewModel? _sidebarThemeVm;

    // The bridge accepts only small, strict JSON objects from the WebView2 renderer.
    private static readonly JsonDocumentOptions WebMessageJsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
    };

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
    private readonly GpnTelemetryService _gpnTelemetry = new();
    private readonly GpnResilienceLog _gpnResilienceLog = new();

    /// <summary>GpnCaptureLoop'un son yayınladığı telemetri anlık görüntüsü (dashboard'a yeniden basmak için).</summary>
    private GpnCaptureStatsSnapshot? _lastCaptureStats;

    // Bağlantı kurulamayınca dashboard hata kartına taşınan başarısızlık bilgisi:
    // ana çekirdeğin son Failed durumu (kullanıcı dostu Error mesajı) + son
    // başlatma teşhisi (teknik ayrıntı / kod) + GPN koordinatörünün son Failed
    // anlık görüntüsü (seçim/launcher hatası — çekirdek hiç başlamamış olabilir).
    private CoreHealthSnapshot? _lastMainCoreFailure;
    private CoreStartupDiagnostic? _lastMainCoreDiagnostic;
    private GpnConnectionSnapshot? _lastGpnFailedSnapshot;

    // GPN PID havuzu köprüsü — SplitTunnelViewModel'in vpn eylemli oyunlarından
    // GpnTargetResolver'ı canlı çalıştırır; dashboard "PID havuzu" kartını besler.
    private GpnTargetResolverBridge? _gpnPidBridge;

    // WARP egress otomatik kurtarma — WARP dial sağlığı faulted olunca aktif WG
    // tünelini yeniden başlatır (bkz. WarpAutoRecoverService).
    private WarpAutoRecoverService? _warpAutoRecover;

    // Last rule-drift verdict pushed to the dashboard ("InSync"/"Drifted"/...).
    // The periodic health check only republishes when the verdict changes, so the
    // banner never flickers on every 30 s tick.
    private string _lastRuleDriftVerdict = "";
    private string _lastNodeSignature = "";
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

    // Real ping-test bridge: runs the native SpeedtestService over the selected
    // profiles and streams per-node results into the WebView2 DOM.
    private SpeedtestService? _nodeSpeedtestService;
    private CancellationTokenSource? _nodePingCancellation;
    private bool _nodeTestRunning;
    private long _nodeTestRunId;
    private readonly ProcessCatalogService _processCatalogService = new();
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
        _dashboardPublisher = new AoGPN.Services.DashboardPublisher(ExecuteScriptSafelyAsync);
        _connectionCoordinator.SnapshotChanged += snapshot =>
        {
            if (!_isClosing)
            {
                _ = Dispatcher.InvokeAsync(() => _dashboardPublisher.PublishAsync(snapshot));
            }
        };
        _dashboardHost.WebMessageReceived += CoreWebView2_WebMessageReceived;
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
            .Subscribe(async evt => await PushGpnDiagAsync(evt));

        AppEvents.GpnCaptureStatsChanged.AsObservable()
            .Subscribe(async evt =>
            {
                _lastCaptureStats = evt;
                await PushGpnCaptureStatsAsync();
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
    /// Parses only JSON string messages and dispatches the supported frontend actions.
    /// Unknown actions and malformed payloads are ignored by design.
    /// </summary>
    private async void CoreWebView2_WebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        string rawMessage;
        try
        {
            // The HTML bridge deliberately calls postMessage with JSON.stringify(...).
            // TryGetWebMessageAsString rejects object messages rather than coercing them.
            rawMessage = e.TryGetWebMessageAsString();
        }
        catch (COMException)
        {
            return;
        }

        // Settings payloads (values + option lists) are far larger than the small
        // node/connection commands, so the cap is raised well above them. The strict
        // JSON options below still reject malformed or deeply nested renderer input.
        if (string.IsNullOrWhiteSpace(rawMessage) || rawMessage.Length > 128 * 1024)
        {
            return;
        }

        if (!DashboardMessageParser.TryParse(rawMessage, out var dashboardMessage)
            || dashboardMessage is null)
        {
            return;
        }

        try
        {
            using var rootDocument = JsonDocument.Parse(rawMessage, WebMessageJsonOptions);
            var root = rootDocument.RootElement;
            var action = dashboardMessage.Action;

            switch (action)
            {
                case "toggle_connection":
                    // Delegate to the existing split-tunnel ViewModel so the click
                    // updates routing rules, persisted settings, TUN requirements and
                    // the normal AoGPN core reload path instead of changing a UI flag.
                    var requestedMode = "gpn";
                    if (TryGetStringProperty(root, "mode", out var requestedModeValue)
                        && requestedModeValue is "vpn" or "gpn")
                    {
                        requestedMode = requestedModeValue;
                    }
                    var requestedTransport = "proxy";
                    if (TryGetStringProperty(root, "transport", out var requestedTransportValue)
                        && requestedTransportValue is "tun" or "proxy")
                    {
                        requestedTransport = requestedTransportValue;
                    }
                    await ToggleConnectionAsync(requestedMode, requestedTransport);
                    break;

                case "gpn_connect":
                    // Belirgin "GPN Bağlan" butonu: seçili profil ne olursa olsun
                    // (WireGuard dışı olsa bile) GPN modu açıkça seçildiğinde
                    // İtalya/Almanya otomatik seçimini zorla tetikle.
                    await RunGpnConnectAsync();
                    break;

                case "gpn_servers_list":
                    // Sunucu Yönetimi ekranı: gpn_servers tablosunun meta verisini
                    // (özel anahtar hariç) + gömülü varsayılan durumunu dashboard'a gönder.
                    await PushGpnServersAsync();
                    await PushGpnDefaultsStatusAsync();
                    break;

                case "gpn_defaults_status":
                    // Gömülü varsayılan şablonların katalog kayıtlarıyla durumu
                    // (tohumlandı mı / anahtar var mı / güncel mi) — saf okuma.
                    await PushGpnDefaultsStatusAsync();
                    break;

                case "gpn_defaults_restore":
                    // "Varsayılanları geri yükle": gömülü anahtarsız şablon alanlarını
                    // mevcut kayıtlara yeniden uygula (DPAPI anahtarı korunur), durumu
                    // ve listeyi tazele.
                    await RestoreGpnDefaultsAsync();
                    break;

                case "gpn_servers_probe":
                case "gpn_cluster_probe":
                    // Canlı ölçüm: gpn_servers'taki etkin sunuculara ICMP ping +
                    // UDP sağlık testi çalıştır ve rozetleri dashboard'a gönder.
                    // gpn_cluster_probe aynı ölçümü GPN panelindeki "sunucu kümesi"
                    // kartı için tetikler — iki akış aynı setGpnServerProbes verisini besler.
                    await ProbeGpnServersAsync();
                    break;

                case "gpn_pid_pool_start":
                    GetGpnPidBridge().Start();
                    await PushGpnPidPoolAsync();
                    break;

                case "gpn_pid_pool_stop":
                    GetGpnPidBridge().Stop();
                    await PushGpnPidPoolAsync();
                    break;

                case "gpn_pid_pool_refresh":
                    // Tek seferlik ölçüm (hedef adlarını da yeniden okur).
                    GetGpnPidBridge().RefreshNow();
                    await PushGpnPidPoolAsync();
                    break;

                case "gpn_server_add":
                    // .conf metnini kataloğa içe aktar (DPAPI ile şifrelenerek saklanır).
                    if (TryGetLongStringProperty(root, "confText", out var gpnConfText))
                    {
                        await ImportGpnServersAsync(gpnConfText);
                    }
                    break;

                case "gpn_server_delete":
                    if (TryGetStringProperty(root, "serverId", out var gpnDeleteId))
                    {
                        await DeleteGpnServerAsync(gpnDeleteId);
                    }
                    break;

                case "gpn_server_toggle":
                    if (TryGetStringProperty(root, "serverId", out var gpnToggleId)
                        && TryGetBooleanProperty(root, "enabled", out var gpnToggleEnabled))
                    {
                        await ToggleGpnServerAsync(gpnToggleId, gpnToggleEnabled);
                    }
                    break;

                case "gpn_server_add_dialog":
                    // WPF ekleme penceresi: DPAPI'li kataloğa manuel sunucu ekle.
                    await ShowGpnServerEditDialogAsync(existingServerId: null);
                    break;

                case "gpn_server_edit_dialog":
                    if (TryGetStringProperty(root, "serverId", out var gpnEditId))
                    {
                        await ShowGpnServerEditDialogAsync(gpnEditId);
                    }
                    break;

                case "select_node":
                    if (!TryGetStringProperty(root, "indexId", out var nodeIndexId))
                    {
                        return;
                    }

                    await SelectNodeAsync(nodeIndexId);
                    break;

                case "copy_nodes":
                    TryGetStringArrayProperty(root, "indexIds", out var copyIds);
                    await CopyNodesAsync(copyIds);
                    break;

                case "paste_nodes":
                    await PasteNodesAsync();
                    break;

                case "delete_nodes":
                    if (!TryGetStringArrayProperty(root, "indexIds", out var deleteIds))
                    {
                        return;
                    }

                    await DeleteNodesAsync(deleteIds);
                    break;

                case "test_nodes":
                    TryGetStringArrayProperty(root, "indexIds", out var testIds);
                    TryGetStringProperty(root, "testType", out var testType);
                    var requestedRunId = TryGetInt64Property(root, "runId", out var parsedRunId)
                        ? parsedRunId
                        : 0;
                    await StartNodeSpeedtestAsync(testIds, testType, requestedRunId);
                    break;

                case "stop_test":
                    if (TryGetInt64Property(root, "runId", out var stopRunId)
                        && stopRunId != Volatile.Read(ref _nodeTestRunId))
                    {
                        return;
                    }
                    StopNodeSpeedtest();
                    break;

                case "disable_nodes":
                    if (!TryGetStringArrayProperty(root, "indexIds", out var disableIds))
                    {
                        return;
                    }

                    await DisableNodesAsync(disableIds);
                    break;

                case "restore_nodes":
                    if (!TryGetStringArrayProperty(root, "indexIds", out var restoreIds))
                    {
                        return;
                    }

                    await RestoreNodesAsync(restoreIds);
                    break;

                case "cleanup_failed":
                    TryGetStringProperty(root, "target", out var cleanupTarget);
                    await CleanupFailedNodesAsync(cleanupTarget == "delete" ? "delete" : "disable");
                    break;

                case "dedup_nodes":
                    await DedupNodesAsync();
                    break;

                case "get_node_pool":
                    await PushNodePoolAsync();
                    break;

                case "add_node_pool_link":
                    if (!TryGetStringProperty(root, "url", out var poolAddUrl) || poolAddUrl.Length < 8)
                    {
                        return;
                    }

                    await AddNodePoolLinkAsync(poolAddUrl);
                    break;

                case "edit_node_pool_link":
                    if (!TryGetStringProperty(root, "url", out var poolEditUrl)
                        || !TryGetStringProperty(root, "newUrl", out var poolNewUrl))
                    {
                        return;
                    }

                    await EditNodePoolLinkAsync(poolEditUrl, poolNewUrl);
                    break;

                case "remove_node_pool_link":
                    if (!TryGetStringProperty(root, "url", out var poolRemoveUrl))
                    {
                        return;
                    }

                    await RemoveNodePoolLinkAsync(poolRemoveUrl);
                    break;

                case "fetch_node_pool":
                    await FetchNodePoolAsync();
                    break;

                case "toggle_node_fav":
                    if (!TryGetStringProperty(root, "indexId", out var favIndexId))
                    {
                        return;
                    }

                    await ToggleNodeFavAsync(favIndexId);
                    break;

                case "set_connection_mode":
                    if (!TryGetStringProperty(root, "mode", out var connectionMode)
                        || connectionMode is not "vpn" and not "gpn")
                    {
                        return;
                    }

                    await SetConnectionModeAsync(connectionMode);
                    break;

                case "set_transport":
                    if (!TryGetStringProperty(root, "transport", out var transportValue)
                        || transportValue is not "tun" and not "proxy")
                    {
                        return;
                    }

                    await SetTransportAsync(transportValue);
                    break;

                case "set_protocol_preference":
                    if (!TryGetStringProperty(root, "protocol", out var protocolPreferenceValue))
                    {
                        return;
                    }

                    await SetProtocolPreferenceAsync(protocolPreferenceValue);
                    break;

                case "set_tun_stack":
                    if (!TryGetStringProperty(root, "stack", out var tunStackValue)
                        || !Global.TunStacks.Contains(tunStackValue))
                    {
                        return;
                    }

                    await SetTunStackAsync(tunStackValue);
                    break;

                case "set_auto_reconnect":
                    await SetAutoReconnectAsync(GetSettingsBool(
                        root,
                        "enabled",
                        AppManager.Instance.Config.ConnectionItem?.AutoReconnectEnabled ?? true));
                    break;

                case "set_gpn_recovery_watch":
                    await SetGpnRecoveryWatchAsync(GetSettingsBool(
                        root,
                        "enabled",
                        AppManager.Instance.Config.GuiItem?.GpnEnableRecoveryWatch ?? true));
                    break;

                case "set_gpn_failover":
                    await SetGpnFailoverAsync(GetSettingsBool(
                        root,
                        "enabled",
                        AppManager.Instance.Config.GuiItem?.GpnEnableFailover ?? false));
                    break;

                case "get_vless_bypass_node":
                    // Çift Bağlantı (Bölünmüş Tünelleme) küresel launcher-bypass düğümünü
                    // dashboard'a gönder (window.setVlessBypassNode).
                    await PushVlessBypassNodeAsync();
                    break;

                case "set_vless_bypass_node":
                    // Dashboard'dan gelen VLESS/Reality launcher-bypass düğümünü doğrula ve
                    // GuiItem.VlessBypassNodeJson'a kaydet — sonraki GPN bağlantısında
                    // (GpnCoreLauncher) mihomo YAML'ine ikincil "vless-launcher" olarak eklenir.
                    await SetVlessBypassNodeAsync(root);
                    break;

                case "set_vless_bypass_from_uri":
                    // Aynı düğüm, ama ham vless:// Reality paylaşım bağlantısı olarak:
                    // FmtHandler.ResolveConfig (kanonik URI ayrıştırıcı) ile çözülür,
                    // VlessProfileItem'a eşlenir ve GuiItem.VlessBypassNodeJson'a kaydedilir.
                    await SetVlessBypassFromUriAsync(root);
                    break;

                case "get_gpn_capture_settings":
                    // WinDivert kuyruk kartı: mevcut GpnCaptureItem ayarlarını dashboard'a gönder.
                    await PushGpnCaptureSettingsAsync();
                    break;

                case "set_gpn_capture_settings":
                    // WinDivert kuyruk kartından gelen ayarları doğrula (saf GpnCaptureSettingsPatch
                    // ile sınırla) ve config'e yaz — sonraki yakalama başlangıcında uygulanır.
                    await SetGpnCaptureSettingsAsync(root);
                    break;

                case "get_gpn_wintun_settings":
                    // Wintun adapter kartı: mevcut GpnWintunItem ayarlarını dashboard'a gönder.
                    await PushGpnWintunSettingsAsync();
                    break;

                case "set_gpn_wintun_settings":
                    // Wintun adapter kartından gelen ayarları doğrula (saf GpnWintunSettingsPatch
                    // ile sanitleştir/sınırla) ve config'e yaz — sonraki bağlantıda uygulanır.
                    await SetGpnWintunSettingsAsync(root);
                    break;

                case "reset_gpn_telemetry":
                    _gpnTelemetry.Reset();
                    await PushGpnTelemetryAsync();
                    break;

                case "get_gpn_telemetry":
                    await PushGpnTelemetryAsync();
                    await PushGpnCaptureStatsAsync();
                    break;

                case "get_gpn_resilience_log":
                    await PushGpnResilienceLogAsync();
                    break;

                case "clear_gpn_resilience_log":
                    _gpnResilienceLog.Clear();
                    await PushGpnResilienceLogAsync();
                    break;

                case "set_active_view":
                    if (TryGetStringProperty(root, "view", out var activeView)
                        && activeView is "dashboard" or "nodes" or "perf" or "boost" or "settings" or "coming")
                    {
                        _activeView = activeView;
                    }
                    break;

                case "request_monitor_snapshot":
                    await PushMonitorSnapshotAsync(force: true);
                    break;

                case "list_running_processes":
                    await PushProcessCatalogAsync();
                    break;

                case "refresh_monitor":
                    if (ViewModel?.ConnectionViewModel is { } monitorViewModel)
                    {
                        await monitorViewModel.Monitor.RefreshAsync();
                        await PushMonitorSnapshotAsync(force: true);
                    }
                    break;

                case "set_app_route":
                    if (!TryGetStringProperty(root, "processName", out var routeProcess)
                        || !TryGetStringProperty(root, "route", out var routeAction)
                        || routeAction is not ("vpn" or "proxy" or "vpn+proxy" or "direct" or "block" or "warp"))
                    {
                        return;
                    }

                    TryGetStringProperty(root, "displayName", out var routeDisplayName);
                    if (ViewModel?.ConnectionViewModel is { } routeViewModel)
                    {
                        var applied = await routeViewModel.SetDashboardAppRouteAsync(routeProcess, routeDisplayName, routeAction);
                        await PushMonitorSnapshotAsync(force: true);
                        await NotifyNodesOpAsync(applied ? "Application route saved" : "Application route was rejected");
                    }
                    break;

                case "move_route":
                    // Game Boost row reordering: rule order = list order (first match
                    // wins), so a domain rule must be movable above the process rule it
                    // takes precedence over. Identifies the entry by type + value so
                    // domain/IP rows are addressable exactly like app rows.
                    if (TryGetStringProperty(root, "entryType", out var moveEntryType)
                        && TryGetStringProperty(root, "value", out var moveEntryValue)
                        && TryGetStringProperty(root, "direction", out var moveDirection)
                        && moveEntryValue.IsNotEmpty()
                        && moveDirection is "up" or "down"
                        && ViewModel?.ConnectionViewModel is { } moveViewModel)
                    {
                        var moved = moveViewModel.MoveManualRoute(moveEntryType, moveEntryValue, moveDirection == "up");
                        await PushMonitorSnapshotAsync(force: true);
                        await NotifyNodesOpAsync(moved
                            ? "Route order updated"
                            : "Route order could not be changed");
                    }
                    break;

                case "add_domain_route":
                    // Manual domain/IP rule from Game Boost (e.g. the one-click
                    // "BSG API → WARP" entry). Duplicate/invalid values are refused
                    // by the view model; the row order stays fully manual.
                    if (TryGetStringProperty(root, "value", out var domainValue)
                        && domainValue.IsNotEmpty()
                        && ViewModel?.ConnectionViewModel is { } domainViewModel)
                    {
                        TryGetStringProperty(root, "route", out var domainAction);
                        TryGetStringProperty(root, "displayName", out var domainDisplayName);
                        var added = domainViewModel.AddDomainRoute(
                            domainValue, domainAction.IsNotEmpty() ? domainAction : "proxy", domainDisplayName);
                        await PushMonitorSnapshotAsync(force: true);
                        await NotifyNodesOpAsync(added
                            ? "Domain route added"
                            : "Domain route could not be added");
                    }
                    break;

                case "add_app":
                    if (ViewModel?.ConnectionViewModel is { } addAppViewModel
                        && UI.OpenFileDialog(out var appPath, "Applications|*.exe|All files|*.*") == true)
                    {
                        await addAppViewModel.AddDashboardAppAsync(appPath);
                        await PushMonitorSnapshotAsync(force: true);
                    }
                    break;

                case "add_running_process":
                    if (root.TryGetProperty("pid", out var pidElement)
                        && pidElement.ValueKind == JsonValueKind.Number
                        && pidElement.TryGetInt32(out var pid)
                        && ViewModel?.ConnectionViewModel is { } runningAppViewModel)
                    {
                        if (_processCatalogService.TryResolveExecutablePath(pid, out var runningPath)
                            && !IsProtectedProcessPath(runningPath))
                        {
                            await runningAppViewModel.AddDashboardAppAsync(runningPath);
                            await PushMonitorSnapshotAsync(force: true);
                            await NotifyNodesOpAsync("Running application added");
                            return;
                        }

                        // Anti-cheat guarded processes (e.g. BattlEye games) deny
                        // executable-path queries entirely. Routing matches by
                        // process name only, so add by name from the picker payload
                        // when native resolution is blocked.
                        TryGetStringProperty(root, "processName", out var pickerProcessName);
                        TryGetStringProperty(root, "displayName", out var pickerDisplayName);
                        pickerProcessName = ProcessCatalogService.NormalizeProcessName(pickerProcessName);
                        if (pickerProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            && !IsProtectedProcessPath(pickerProcessName))
                        {
                            var suggested = KnownAppCatalog.SuggestAction(pickerProcessName, pickerDisplayName ?? string.Empty);
                            var addedByName = await runningAppViewModel.SetDashboardAppRouteAsync(
                                pickerProcessName, pickerDisplayName, suggested);
                            await PushMonitorSnapshotAsync(force: true);
                            await NotifyNodesOpAsync(addedByName
                                ? "Running application added"
                                : "Running application was rejected");
                            return;
                        }

                        await NotifyNodesOpAsync("Running application could not be resolved");
                    }
                    break;

                case "remove_app":
                    if (TryGetStringProperty(root, "processName", out var removeProcessName)
                        && ViewModel?.ConnectionViewModel is { } removeAppViewModel)
                    {
                        var target = removeAppViewModel.Apps
                            .FirstOrDefault(a => a.EntryType == "app" &&
                                a.Value.Equals(removeProcessName, StringComparison.OrdinalIgnoreCase));
                        if (target is not null)
                        {
                            removeAppViewModel.SelectedApp = target;
                            removeAppViewModel.RemoveAppCmd.Execute().Subscribe();
                            await PushMonitorSnapshotAsync(force: true);
                        }
                    }
                    break;

                case "add_files":
                    if (ViewModel?.ConnectionViewModel is { } dropViewModel
                        && root.TryGetProperty("files", out var filesEl)
                        && filesEl.ValueKind == JsonValueKind.Array)
                    {
                        var fileNames = filesEl.EnumerateArray()
                            .Where(f => f.ValueKind == JsonValueKind.String)
                            .Select(f => f.GetString()!)
                            .Where(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                        if (fileNames.Length > 0)
                        {
                            var resolved = await ResolveDropFilePathsAsync(fileNames);
                            if (resolved.Length > 0)
                            {
                                await dropViewModel.AddDroppedFilesAsync(resolved);
                                await PushMonitorSnapshotAsync(force: true);
                            }
                        }
                    }
                    break;

                case "set_auto_game_connect":
                    if (ViewModel?.ConnectionViewModel is { } autoGameViewModel)
                    {
                        autoGameViewModel.AutoConnectOnGameStart = GetSettingsBool(
                            root,
                            "enabled",
                            autoGameViewModel.AutoConnectOnGameStart);
                        await PushMonitorSnapshotAsync(force: true);
                    }
                    break;

                case "set_split_mode":
                    if (TryGetStringProperty(root, "mode", out var splitMode))
                    {
                        await SetDashboardModeAsync(splitMode);
                    }
                    break;

                case "set_split_direction":
                    if (TryGetStringProperty(root, "invert", out var invertManualValue)
                        && ViewModel?.ConnectionViewModel is { } directionVm)
                    {
                        var invert = invertManualValue.Equals("true", StringComparison.OrdinalIgnoreCase);
                        if (directionVm.InvertManualRouting != invert)
                        {
                            directionVm.InvertManualRouting = invert;
                            await directionVm.ApplyCmd.Execute().ToTask();
                            await PushMonitorSnapshotAsync(force: true);
                        }
                    }
                    break;

                case "test_route":
                    if (!TryGetStringProperty(root, "exeName", out var testExe)
                        || !TryGetStringProperty(root, "destination", out var testDestination))
                    {
                        return;
                    }

                    TryGetStringProperty(root, "port", out var testPort);
                    TryGetStringProperty(root, "network", out var testNetwork);
                    TryGetStringProperty(root, "exePath", out var testExePath);
                    await RunRouteTestAsync(testExe, testDestination, testPort, testNetwork, testExePath);
                    break;

                case "set_verbose_log":
                    var verboseEnabled = GetSettingsBool(
                        root,
                        "enabled",
                        AppManager.Instance.Config.GuiItem.EnableVerboseLog);
                    AppManager.Instance.Config.GuiItem.EnableVerboseLog = verboseEnabled;
                    Logging.VerboseLoggingEnabled(verboseEnabled);
                    ConfigSaveQueue.RequestSave(AppManager.Instance.Config);
                    Logging.VerboseIf(verboseEnabled, "GPN", "verbose_log", verboseEnabled ? "enabled" : "disabled");
                    break;

                case "set_effects_tier":
                    // Visual-effects tier from the dashboard segmented control:
                    // "full" (everything), "balanced" (ambient loops frozen,
                    // reactive effects stay) or "reduced" (all off). Applies
                    // immediately on the renderer side, no restart needed.
                    var effectsTier = TryGetStringProperty(root, "tier", out var tierValue)
                        ? NormalizeEffectsMode(tierValue)
                        : "full";
                    AppManager.Instance.Config.GuiItem.EffectsMode = effectsTier;
                    AppManager.Instance.Config.GuiItem.ReduceEffects = effectsTier == "reduced";
                    ConfigSaveQueue.RequestSave(AppManager.Instance.Config);
                    await PushEffectsTierAsync();
                    break;

                case "set_reduce_effects":
                    // Legacy single switch from older dashboard builds: map onto
                    // the effects tier (reduced kills everything, full restores).
                    var reduceEffectsEnabled = GetSettingsBool(
                        root,
                        "enabled",
                        AppManager.Instance.Config.GuiItem.ReduceEffects);
                    AppManager.Instance.Config.GuiItem.EffectsMode = reduceEffectsEnabled ? "reduced" : "full";
                    AppManager.Instance.Config.GuiItem.ReduceEffects = reduceEffectsEnabled;
                    ConfigSaveQueue.RequestSave(AppManager.Instance.Config);
                    await PushEffectsTierAsync();
                    break;

                case "set_language":
                    if (TryGetStringProperty(root, "lang", out var newLang)
                        && Global.Languages.Contains(newLang)
                        && AppManager.Instance.Config.UiItem.CurrentLanguage != newLang)
                    {
                        AppManager.Instance.Config.UiItem.CurrentLanguage = newLang;
                        Thread.CurrentThread.CurrentUICulture = new(newLang);
                        ConfigSaveQueue.RequestSave(AppManager.Instance.Config);
                        await PushLanguageAsync();
                    }
                    break;

                case "set_theme":
                    if (TryGetStringProperty(root, "theme", out var webTheme)
                        && TryMapWebThemeToWpf(webTheme, out var wpfTheme)
                        && AppManager.Instance.Config.UiItem.CurrentTheme != wpfTheme)
                    {
                        // Reuse the native ViewModel so Material Design resources,
                        // the title-bar border and the WebView event channel all update
                        // through the same path as the native theme selector.
                        if (_sidebarThemeVm is not null)
                        {
                            _sidebarThemeVm.CurrentTheme = wpfTheme;
                            cmbSidebarTheme.SelectedValue = wpfTheme;
                        }
                        else
                        {
                            AppManager.Instance.Config.UiItem.CurrentTheme = wpfTheme;
                            ConfigSaveQueue.RequestSave(AppManager.Instance.Config);
                        }
                    }
                    break;

                case "set_system_proxy_mode":
                    var requestedProxyMode = (ESysProxyType)Math.Clamp(
                        GetSettingsInt(root, "mode", (int)(AppManager.Instance.Config.SystemProxyItem?.SysProxyType ?? ESysProxyType.ForcedClear)),
                        0,
                        3);
                    await SetSystemProxyModeAsync(requestedProxyMode);
                    break;

                case "toggle_system_proxy":
                    await ToggleSystemProxyAsync();
                    break;

                case "test_proxy":
                    await TestProxyAsync();
                    break;

                case "performance_sample":
                    if (root.TryGetProperty("payload", out var performancePayload)
                        && performancePayload.ValueKind == JsonValueKind.Object)
                    {
                        var perfJson = performancePayload.GetRawText();
                        DiagLog.Write($"WEBVIEW_PERF {perfJson}");
                    }
                    break;

                case "check_ip":
                    await CheckIpAsync();
                    break;

                case "app_control":
                    if (!TryGetStringProperty(root, "command", out var command))
                    {
                        return;
                    }

                    HandleAppControl(command);
                    break;

                case "set_window_behavior":
                    // Dashboard window-behaviour toggles (minimize-to-tray /
                    // hide-to-tray-on-close) apply immediately on change, so the
                    // shown switch always matches what X / minimize actually do —
                    // the close and minimize paths read this same live config
                    // object, and the queued save persists the new value to disk.
                    var trayConfig = AppManager.Instance.Config;
                    var requestedHideOnClose = GetSettingsBool(root, "hide2TrayWhenClose", trayConfig.UiItem.Hide2TrayWhenClose);
                    var requestedMinimize2Tray = GetSettingsBool(root, "minimize2Tray", trayConfig.UiItem.Minimize2Tray);
                    if (requestedHideOnClose != trayConfig.UiItem.Hide2TrayWhenClose
                        || requestedMinimize2Tray != trayConfig.UiItem.Minimize2Tray)
                    {
                        trayConfig.UiItem.Hide2TrayWhenClose = requestedHideOnClose;
                        trayConfig.UiItem.Minimize2Tray = requestedMinimize2Tray;
                        ConfigSaveQueue.RequestSave(trayConfig);
                        Logging.VerboseIf(
                            requestedHideOnClose || requestedMinimize2Tray,
                            "UI", "window_behavior",
                            $"hideOnClose={requestedHideOnClose} minimize2Tray={requestedMinimize2Tray}");
                        // Reflect the persisted truth back so the form cannot drift
                        // from the real window behaviour.
                        await PushSettingsAsync();
                    }
                    break;

                case "get_settings":
                    await PushSettingsAsync();
                    break;

                case "save_settings":
                    await SaveSettingsAsync(root);
                    break;
            }
        }
        catch (JsonException)
        {
            // Invalid JSON is untrusted renderer input; do not let it reach application
            // logic and do not turn repeated malformed messages into log noise.
        }
        catch (Exception ex)
        {
            // Keep renderer failures contained even if WebView2 is closing concurrently.
            if (!_isClosing)
            {
                Logging.SaveLog("AoGPN WebView2 message handling failed", ex);
            }
        }
    }

    private static bool TryGetStringProperty(
        JsonElement objectElement,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!objectElement.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 64)
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static bool TryGetInt64Property(JsonElement objectElement, string propertyName, out long value)
    {
        value = 0;
        return objectElement.TryGetProperty(propertyName, out var property)
            && property.TryGetInt64(out value)
            && value > 0;
    }

    private static bool TryGetBooleanProperty(JsonElement objectElement, string propertyName, out bool value)
    {
        value = false;
        if (!objectElement.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }
        if (property.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }
        return false;
    }

    /// <summary>
    /// .conf metni gibi uzun payload'lar için: 64 karakterlik kısa-string limiti
    /// conf bloğunu keserdi, bu yüzden ayrı bir okuma yolu (128 KB renderer limiti
    /// içinde). Yalnızca güvenilen eylemler (gpn_server_add) kullanır.
    /// </summary>
    private static bool TryGetLongStringProperty(
        JsonElement objectElement,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (!objectElement.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 128 * 1024)
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static bool TryGetStringArrayProperty(
        JsonElement objectElement,
        string propertyName,
        out string[] values)
    {
        values = [];
        if (!objectElement.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var result = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var candidate = item.GetString();
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length <= 64)
            {
                result.Add(candidate);
            }
        }

        values = result.ToArray();
        return values.Length > 0;
    }
/// <summary>
    /// Extracts a two-letter country code from common v2ray node remark patterns.
    /// Matches known codes surrounded by separators like [TR], TR-, (TR), TR·.
    /// Returns empty string when no country hint is found.
    /// </summary>
    private static string ExtractCountryFromRemarks(string? remarks)
    {
        if (string.IsNullOrWhiteSpace(remarks))
            return string.Empty;

        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TR", "US", "DE", "FR", "GB", "NL", "JP", "KR", "SG", "HK",
            "CA", "AU", "RU", "IR", "AE", "BR", "IN", "CN", "TW", "IT",
            "ES", "SE", "CH", "PL", "CZ", "UA", "KZ", "VN", "TH", "ID",
            "MY", "PH", "AR", "CL", "CO", "MX", "ZA", "NG", "EG",
        };

        var remarkUpper = remarks.ToUpperInvariant();
        foreach (var code in codes)
        {
            var idx = remarkUpper.IndexOf(code, StringComparison.Ordinal);
            while (idx >= 0)
            {
                var before = idx > 0 ? remarkUpper[idx - 1] : '.';
                var after = idx + 2 < remarkUpper.Length ? remarkUpper[idx + 2] : '.';
                if (!char.IsLetterOrDigit(before) && !char.IsLetterOrDigit(after))
                    return code;
                idx = remarkUpper.IndexOf(code, idx + 1, StringComparison.Ordinal);
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Best-effort country code for a node: a two-letter code in the remarks wins;
    /// otherwise the node address is resolved through GeoIP when it is a plain IP.
    /// </summary>
    private static string ResolveNodeCountry(string? remarks, string? address)
    {
        var fromRemarks = ExtractCountryFromRemarks(remarks);
        if (fromRemarks.IsNotEmpty())
        {
            return fromRemarks;
        }

        var normalizedAddress = NormalizeNodeAddress(address);
        if (normalizedAddress.IsNotEmpty()
            && IPAddress.TryParse(normalizedAddress, out var ip)
            && !IsNonPublicAddress(ip))
        {
            var geo = GeoIpLookupService.Lookup(ip);
            return geo.CountryCode?.Trim().ToUpperInvariant() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string NormalizeNodeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return string.Empty;
        var value = address.Trim();
        if (value.StartsWith('[') && value.IndexOf(']') is var end && end > 1)
            return value[1..end];
        if (value.Count(c => c == ':') == 1 && value.LastIndexOf(':') is var colon && IPAddress.TryParse(value[..colon], out _))
            return value[..colon];
        return value.TrimEnd('.');
    }

    private static bool IsNonPublicAddress(IPAddress ip)
    {
        return GeoIpLookupService.Lookup(ip).IsPrivate;
    }


    /// <summary>
    /// Applies the same mode transition used by the native ConnectionView. The command
    /// is serialized so repeated clicks cannot overlap rule writes or core reloads.
    /// </summary>
    private async Task ToggleConnectionAsync(string requestedMode, string transport)
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
    private async Task RunGpnConnectAsync()
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

            var connectionViewModel = ViewModel?.ConnectionViewModel;
            if (connectionViewModel is null)
            {
                return;
            }
            var appNames = connectionViewModel.Apps
                .Where(a => a.EntryType == "app")
                .Select(a => a.ProcessName.IsNotEmpty() ? a.ProcessName : a.Value)
                .Where(n => n.IsNotEmpty())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
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
                connectionViewModel = ViewModel?.ConnectionViewModel;
                if (connectionViewModel is null)
                {
                    return;
                }
                foreach (var app in connectionViewModel.Apps)
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

            await PushMonitorSnapshotAsync(force: true);
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
    private async Task SetConnectionModeAsync(string mode)
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
    private async Task SetDashboardModeAsync(string mode)
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
    private async Task SetTransportAsync(string transport)
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

            if (transport == "tun" && !AllowEnableTun())
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
    private async Task SetProtocolPreferenceAsync(string preference)
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
    private async Task SetTunStackAsync(string stack)
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
    private async Task SetAutoReconnectAsync(bool enabled)
    {
        var config = AppManager.Instance.Config;
        config.ConnectionItem ??= new();
        config.ConnectionItem.AutoReconnectEnabled = enabled;
        await ConfigSaveQueue.SaveAndWaitAsync(config);
        await PushSettingsAsync();
    }

    private async Task SetGpnRecoveryWatchAsync(bool enabled)
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
    private async Task SetGpnFailoverAsync(bool enabled)
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
    private async Task SetVlessBypassNodeAsync(JsonElement root)
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
    private async Task SetVlessBypassFromUriAsync(JsonElement root)
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
    private async Task PushVlessBypassNodeAsync()
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
    private async Task PushGpnCaptureSettingsAsync()
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
    private async Task SetGpnCaptureSettingsAsync(JsonElement root)
    {
        var patch = new GpnCaptureSettingsPatch(
            QueueLen: ToUintOrNull(TryGetIntProperty(root, "queueLen")),
            QueueTime: ToUintOrNull(TryGetIntProperty(root, "queueTime")),
            QueueSize: ToUintOrNull(TryGetIntProperty(root, "queueSize")),
            EnableQueueLen: TryGetBooleanProperty(root, "enableQueueLen", out var enableLen) ? enableLen : null,
            EnableQueueTime: TryGetBooleanProperty(root, "enableQueueTime", out var enableTime) ? enableTime : null,
            EnableQueueSize: TryGetBooleanProperty(root, "enableQueueSize", out var enableSize) ? enableSize : null,
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
    private async Task PushGpnWintunSettingsAsync()
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
    private async Task SetGpnWintunSettingsAsync(JsonElement root)
    {
        var patch = new GpnWintunSettingsPatch(
            AdapterName: TryGetStringProperty(root, "adapterName", out var adapterName) ? adapterName : null,
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

    private async Task SetSystemProxyModeAsync(ESysProxyType requestedType)
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

    private async Task ToggleSystemProxyAsync()
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

    private void HandleAppControl(string command)
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
    private string _lastSystemProxySignature = "";

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
    private static bool TryMapWebThemeToWpf(string webTheme, out string wpfTheme)
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

    /// <summary>
    /// Pushes the visual-effects tier (full / balanced / reduced) to the
    /// dashboard. Legacy configs (empty EffectsMode plus the old ReduceEffects
    /// switch) are normalized and persisted so the file self-migrates; the tier
    /// applies immediately on the renderer side, no restart needed. The host
    /// config is authoritative — the renderer never persists it.
    /// </summary>
    private async Task PushEffectsTierAsync()
    {
        var config = AppManager.Instance.Config;
        var tier = NormalizeEffectsMode(config.GuiItem.EffectsMode);
        config.GuiItem.ReduceEffects = tier == "reduced";
        if (!string.Equals(config.GuiItem.EffectsMode, tier, StringComparison.Ordinal))
        {
            config.GuiItem.EffectsMode = tier;
            ConfigSaveQueue.RequestSave(config);
        }
        await ExecuteScriptSafelyAsync(
            $"if(typeof setEffectsTier==='function'){{setEffectsTier({JsonSerializer.Serialize(tier)},false)}}");
    }

    /// <summary>
    /// Accepts "full", "balanced" or "reduced"; anything else falls back to
    /// "full" (or "reduced" when the legacy ReduceEffects switch was on and the
    /// mode was never set) — a null/empty mode from an older config file migrates
    /// without losing the user's previous choice.
    /// </summary>
    private static string NormalizeEffectsMode(string? mode)
    {
        if (mode is "balanced" or "reduced")
        {
            return mode;
        }
        return AppManager.Instance.Config.GuiItem.ReduceEffects ? "reduced" : "full";
    }

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

    /// <summary>
    /// Pushes the current WPF UI language to the dashboard so the language dropdown
    /// and any future localised strings in the WebView stay in sync.
    /// </summary>
    private async Task PushLanguageAsync()
    {
        var lang = AppManager.Instance.Config.UiItem.CurrentLanguage ?? "en";
        await ExecuteScriptSafelyAsync(
            $"if(typeof applyLanguage==='function'){{applyLanguage('{lang}')}}");
    }

    /// <summary>
    /// Pushes the running build/version to the dashboard so the About &amp; Help
    /// page always reports the real application version (it lifts it straight out
    /// of the assembly, exactly like the window title and splash screen).
    /// </summary>
    private async Task PushAppInfoAsync()
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            version = Utils.GetVersionInfo(),
            appName = "AO GPN Desktop",
            arch = RuntimeInformation.ProcessArchitecture.ToString()
        });
        await ExecuteScriptSafelyAsync($"window.setAppInfo?.({payload});");
    }

    /// <summary>
    /// Resolves EXE file names dropped onto the Game Boost page to full paths.
    /// Checks running processes first, then falls back to common install locations.
    /// </summary>
    private static async Task<string[]> ResolveDropFilePathsAsync(string[] exeNames)
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
    private async Task TestProxyAsync()
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
    private async Task CheckIpAsync()
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

    /// <summary>
    /// Publishes both the persisted independent preference and the effective OS mode.
    /// A connection using proxy transport can temporarily force the effective mode;
    /// showing both values prevents the quick-toggle from appearing to lose its choice.
    /// </summary>
    private async Task PushSystemProxyStateAsync(bool force = false)
    {
        if (!_webViewReady)
        {
            return;
        }

        var config = AppManager.Instance.Config;
        var desired = config.SystemProxyItem?.SysProxyType ?? ESysProxyType.ForcedClear;
        var effective = SystemProxyPolicy.ResolveEffectiveType(config);
        var connectionOwns = SystemProxyPolicy.ConnectionNeedsSystemProxy(config);
        var signature = $"{(int)desired}:{(int)effective}:{connectionOwns}";
        if (!force && signature == _lastSystemProxySignature)
        {
            return;
        }

        _lastSystemProxySignature = signature;
        await ExecuteScriptSafelyAsync(
            $"window.setSystemProxyState({JsonSerializer.Serialize((int)desired)}, {JsonSerializer.Serialize((int)effective)}, {JsonSerializer.Serialize(connectionOwns)});");
    }

    /// <summary>
    /// Sends the currently selected AoGPN profile to the dashboard node card.
    /// The signature check suppresses repeats so the 2 s poll only touches the
    /// browser when the name, address or protocol actually changed.
    /// </summary>
    private async Task PushNodeInfoAsync(bool force = false)
    {
        if (!_webViewReady)
        {
            return;
        }

        ProfileItem? profile;
        try
        {
            profile = await AppManager.Instance.GetProfileItem(AppManager.Instance.Config.IndexId);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN node info lookup failed", ex);
            return;
        }

        if (profile is null)
        {
            return;
        }

        var address = profile.Port > 0 ? $"{profile.Address}:{profile.Port}" : profile.Address;
        var signature = $"{profile.Remarks}|{address}|{profile.ConfigType}";
        if (!force && signature == _lastNodeSignature)
        {
            return;
        }

        _lastNodeSignature = signature;
        var nameJson = JsonSerializer.Serialize(profile.Remarks);
        var addressJson = JsonSerializer.Serialize(address);
        var protocolJson = JsonSerializer.Serialize(profile.ConfigType.ToString());
        await ExecuteScriptSafelyAsync(
            $"window.updateNodeInfo({nameJson}, {addressJson}, {protocolJson});");
    }

    /// <summary>
    /// Publishes the live connection table and manual-route list used by the
    /// GlassWire-style Performance and Game Boost views. The renderer receives a
    /// read-only snapshot; all mutations return through SplitTunnelViewModel so the
    /// native routing/config persistence path remains authoritative.
    /// </summary>
    private static bool IsProtectedProcessPath(string path)
    {
        var name = ProcessCatalogService.NormalizeProcessName(path);
        return name is "aogpn.exe" or "xray.exe" or "sing-box.exe" or "mihomo.exe" or "v2ray.exe" or "openvpn.exe";
    }

    /// <summary>
    /// Runs the offline route test on a background thread and pushes the result into
    /// the dashboard. The test rebuilds the sing-box config the core would use right
    /// now and simulates rule matching — no traffic is sent and no DNS is resolved.
    /// </summary>
    private async Task RunRouteTestAsync(
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

    /// <summary>
    /// Pushes a GPN resilience decision (server switch, UDP death, mode fallback to
    /// V2rayTCP, or Tier-2 recovery) into the dashboard. The event carries no traffic;
    /// it only surfaces what GpnServerSelectionService already decided, in camelCase.
    /// </summary>
    /// <summary>
    /// Pushes a GPN diagnostic line (GPN_LOG / GPN_RECOVER / GPN_SELECT ...) into
    /// the dashboard diagnostics feed. The event is published by DiagLog for every
    /// "GPN_*" write, so the feed mirrors ao_diag.txt without re-reading the file.
    /// </summary>
    private async Task PushGpnDiagAsync(GpnDiagEvent evt)
    {
        if (!_webViewReady)
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

    private async Task PushGpnTelemetryAsync()
    {
        if (!_webViewReady)
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
    private async Task PushGpnCaptureStatsAsync()
    {
        if (!_webViewReady)
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
            _gpnPidBridge = new GpnTargetResolverBridge(ViewModel!.ConnectionViewModel);
            _gpnPidBridge.SnapshotChanged += async _ => await PushGpnPidPoolAsync();
        }
        return _gpnPidBridge;
    }

    /// <summary>PID havuzu anlık görüntüsünü dashboard'a gönderir (özel veri taşınmaz).</summary>
    private async Task PushGpnPidPoolAsync()
    {
        if (!_webViewReady)
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
    private async Task PushWarpHealthAsync()
    {
        var health = WarpDialHealthMonitor.Instance.Snapshot;
        var json = JsonSerializer.Serialize(
            new
            {
                health.Faulted,
                health.ErrorCount,
                health.LatestError,
            },
            RouteTestJsonOptions);
        await ExecuteScriptSafelyAsync($"window.setWarpHealth?.({json});");
    }

    private async Task PushWinDivertHealthAsync()
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

    private async Task PushGpnResilienceLogAsync()
    {
        if (!_webViewReady)
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
    private async Task PushGpnDrainAsync(GpnDrainSnapshot snap)
    {
        if (!_webViewReady)
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
    private async Task PushGpnResilienceAsync(GpnResilienceEvent evt)
    {
        if (!_webViewReady)
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
    private async Task PushAvailabilityInfoAsync(AvailabilityCheckResult result)
    {
        if (!_webViewReady)
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

    // ── GPN Sunucu Yönetimi (Faz 3 ekranı) ───────────────────────────────

    /// <summary>
    /// gpn_servers tablosunun meta verisini (özel anahtar HARİÇ — şifreli blob
    /// dahi renderer'a gönderilmez) dashboard Sunucu Yönetimi ekranına gönderir.
    /// </summary>
    private async Task PushGpnServersAsync()
    {
        if (!_webViewReady)
        {
            return;
        }

        var items = await WireGuardServerCatalog.GetItemsAsync();
        var view = (items ?? []).Select(item => new
        {
            ServerId = item.ServerId,
            Name = item.Name,
            EndpointHost = item.EndpointHost,
            EndpointPort = item.EndpointPort,
            ClientAddress = item.ClientAddress,
            Mtu = item.Mtu,
            Dns = item.Dns,
            Keepalive = item.Keepalive,
            IsEnabled = item.IsEnabled,
            KeyProtected = item.ClientPrivateKeyEnc.IsNotEmpty(),
            UpdatedAt = item.UpdatedAt,
        }).ToList();

        try
        {
            var json = JsonSerializer.Serialize(view, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setGpnServers?.({json});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-servers push failed", ex);
        }
    }

    /// <summary>
    /// gpn_servers'taki etkin sunuculara canlı ölçüm yapar ve sonucu dashboard'a
    /// gönderir: <see cref="GpnServerSelectionService.ProbeAllAsync"/> (paralel ICMP
    /// ping → gecikme/kayıp) + <see cref="UdpHealthChecker"/> (51820/udp yolu).
    /// Ölçüm yalnızca okuma amaçlıdır — tünel başlatılmaz. Özel anahtar renderer'a
    /// hiç gönderilmez.
    /// </summary>
    private async Task ProbeGpnServersAsync()
    {
        if (!_webViewReady)
        {
            return;
        }

        try
        {
            var servers = await WireGuardServerCatalog.LoadAsync();
            var enabled = servers.Where(s => s.IsEnabled).ToList();

            // Ölçüm, tek/önbellekli GpnServerSelectionService örneği üzerinden
            // MainWindowViewModel.ProbeServersAsync ile yürütülür.
            var probeResults = await ViewModel!.ProbeServersAsync(
                enabled,
                new GpnProbeOptions
                {
                    Samples = 3,
                    PerSampleTimeoutMs = 1000,

                    // Tünel (TUN auto_route + strict_route) etkinken ölçümü FİZİKSEL
                    // NIC üzerinden yap: ICMP atlanır (Ping arayüz bağlayamaz — tünel-içi
                    // ping anlamsız), UDP/TCP probe soketleri ProbeEgressNic ile fiziksel
                    // uplink'e bağlanır → kendi tünelinin içine yakalanmaz, gerçek sunucu
                    // erişilebilirliği ölçülür. Tünel yoksa no-op (normal ICMP + ölçüm).
                    EscapeTunnelForProbes = true,
                });

            // UDP sağlık testi — seçim/failover ile AYNI kanıt zinciri (el sıkışma +
            // junk): WireGuard sunucusu junk pakete yanıt vermediği için yalnızca
            // geçerli el sıkışma Open + RTT üretir; panel gecikme fallback'ini ve
            // gerçek UDP durumunu buradan besler.
            var udpList = await ViewModel!.ProbeUdpAllAsync(
                enabled,
                new GpnProbeOptions
                {
                    // El sıkışma UDP'si hafif zaman aşımıyla — ölçüm paneli hızlı kalsın.
                    HandshakeProbe = new WireGuardHandshakeProbeOptions(WaitTimeoutMs: 1500, MaxAttempts: 1),
                    UdpCheck = new UdpHealthCheckOptions(WaitTimeoutMs: 1500),
                    EscapeTunnelForProbes = true,
                });
            var udpResults = udpList.Select(u => (u.ServerId, Result: u)).ToList();

            var view = probeResults.Select(p => new
            {
                ServerId = p.ServerId,
                DelayMs = p.DelayMs,
                AvgDelayMs = p.AvgDelayMs,
                MaxDelayMs = p.MaxDelayMs,
                LossPercent = p.LossPercent,
                IsSuccess = p.IsSuccess,
                // Tünel etkinken ölçüm fiziksel NIC üzerinden yapıldı (ICMP atlandı) —
                // dashboard küme kartı bunu kullanıcıya ipucu olarak gösterir.
                MeasuredOverPhysicalNic = p.MeasuredOverPhysicalNic,
                UdpStatus = udpResults.FirstOrDefault(u => u.ServerId == p.ServerId).Result?.Status.ToString() ?? "unknown",
                UdpRoundTripMs = udpResults.FirstOrDefault(u => u.ServerId == p.ServerId).Result?.RoundTripMs,
                MeasuredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }).ToList();

            var json = JsonSerializer.Serialize(view, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setGpnServerProbes?.({json});");

            // ── Failover matrisi: aynı ölçümü varsayılan + katı politika altında ──
            // Aynı GpnServerSelectionService (saf DecideFailover) ile hesaplanır;
            // dashboard yalnızca görüntüler. Aktif sunucu: bağlıysa o, değilse aday.
            await PushGpnFailoverMatrixAsync(enabled, probeResults, udpResults);

            // ── En iyi aday: GPN Bağlan'ın yapacağı otomatik seçim kararını önceden
            // göster (saf DecideSelection — SelectBestServerAsync ile birebir).
            await PushGpnSelectionPredictionAsync(enabled, probeResults, udpResults);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-servers probe failed", ex);
        }
    }

    /// <summary>
    /// Failover karar matrisini dashboard'a taşır: ping + UDP ölçümleri iki politika
    /// altında (varsayılan / katı) GpnServerSelectionService.EvaluateFailoverMatrix ile
    /// değerlendirilir ve setGpnFailoverMatrix olarak yayınlanır.
    /// </summary>
    private async Task PushGpnFailoverMatrixAsync(
        IReadOnlyList<GpnServerProfile> enabled,
        IReadOnlyList<GpnServerProbeResult> probeResults,
        List<(string ServerId, UdpProbeResult Result)> udpResults)
    {
        if (!_webViewReady || enabled.Count == 0)
        {
            return;
        }

        try
        {
            var udpMap = udpResults.ToDictionary(u => u.ServerId, u => u.Result);

            // Aktif: bağlı sunucu; bağlı değilse en düşük ping'li aday (matris "eğer
            // şu an bağlı olsaydık" senaryosunu gösterir).
            var active = ViewModel!.CurrentGpnServer
                ?? enabled.OrderBy(s => probeResults.FirstOrDefault(r => r.ServerId == s.ServerId)?.DelayMs ?? int.MaxValue)
                    .First();

            var matrix = ViewModel!.EvaluateFailoverMatrix(
                active, enabled, probeResults, udpMap,
                new GpnProbeOptions { Samples = 3, PerSampleTimeoutMs = 1000 });

            var json = JsonSerializer.Serialize(matrix, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setGpnFailoverMatrix?.({json});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-failover-matrix push failed", ex);
        }
    }

    /// <summary>
    /// "En iyi aday" kartını dashboard'a taşır: aynı canlı ölçüm (ping + UDP) üzerinden
    /// GpnServerSelectionService.EvaluateSelection (saf DecideSelection) ile GPN
    /// Bağlan'ın yapacağı otomatik seçim kararı önceden gösterilir. Durum/mod adları
    /// string olarak serileştirilir (enum numarası değil) — renderer bunları karşılaştırır.
    /// </summary>
    private async Task PushGpnSelectionPredictionAsync(
        IReadOnlyList<GpnServerProfile> enabled,
        IReadOnlyList<GpnServerProbeResult> probeResults,
        List<(string ServerId, UdpProbeResult Result)> udpResults)
    {
        if (!_webViewReady || enabled.Count == 0)
        {
            return;
        }

        try
        {
            var udpMap = udpResults.ToDictionary(u => u.ServerId, u => u.Result);
            var prediction = ViewModel!.EvaluateSelection(
                enabled, probeResults, udpMap,
                new GpnProbeOptions { Samples = 3, PerSampleTimeoutMs = 1000 });

            var json = JsonSerializer.Serialize(new
            {
                prediction.Best?.ServerId,
                prediction.Best?.Name,
                PingMs = probeResults.FirstOrDefault(r => r.ServerId == prediction.Best?.ServerId)?.DelayMs ?? -1,
                LossPercent = probeResults.FirstOrDefault(r => r.ServerId == prediction.Best?.ServerId)?.LossPercent ?? 100,
                UdpStatus = prediction.UdpProbe?.Status.ToString() ?? "unknown",
                Mode = prediction.Mode.ToString(),
                Reason = prediction.Reason,
                Servers = enabled.Select(s => new
                {
                    s.ServerId,
                    s.Name,
                    DelayMs = probeResults.FirstOrDefault(r => r.ServerId == s.ServerId)?.DelayMs ?? -1,
                    LossPercent = probeResults.FirstOrDefault(r => r.ServerId == s.ServerId)?.LossPercent ?? 100,
                    UdpStatus = udpResults.FirstOrDefault(u => u.ServerId == s.ServerId).Result?.Status.ToString() ?? "unknown",
                    Selected = s.ServerId == prediction.Best?.ServerId,
                }).ToList(),
                MeasuredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setGpnSelectionPrediction?.({json});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-selection-prediction push failed", ex);
        }
    }

    /// <summary>
    /// "Varsayılanları geri yükle" aksiyonu: gömülü anahtarsız şablon alanlarını
    /// (sunucu genel anahtarı, adres, MTU, DNS, keepalive) mevcut gpn_servers
    /// kayıtlarına yeniden uygular — DPAPI'li istemci anahtarı, ad ve etkinlik
    /// korunur. Sonrasında durum + sunucu listesi + kullanıcı bildirimi gönderilir.
    /// </summary>
    private async Task RestoreGpnDefaultsAsync()
    {
        var result = await WireGuardServerCatalog.RestoreDefaultsAsync();
        await PushGpnDefaultsStatusAsync(result);
        await PushGpnServersAsync();

        var message = result.RestoredCount > 0
            ? $"Varsayılanlar geri yüklendi ({result.RestoredCount} sunucu güncellendi)."
            : "Varsayılanlar zaten güncel — güncelleme gerekmedi.";
        await ExecuteScriptSafelyAsync($"window.gpnServersNotify?.({JsonSerializer.Serialize(message, RouteTestJsonOptions)});");
    }

    /// <summary>
    /// Gömülü varsayılan şablonların durumunu dashboard'a gönderir: her şablon için
    /// tohumlandı mı / anahtar var mı / güncel mi + farklı alan adları. Özel anahtar
    /// içeriği asla gönderilmez (yalnızca KeyPresent bayrağı).
    /// </summary>
    private async Task PushGpnDefaultsStatusAsync(WireGuardServerCatalog.GpnDefaultsRestoreResult? restoreResult = null)
    {
        if (!_webViewReady)
        {
            return;
        }

        try
        {
            IReadOnlyList<WireGuardServerCatalog.GpnDefaultsStatus> statuses = restoreResult is not null
                ? restoreResult.Statuses
                : await WireGuardServerCatalog.GetDefaultsStatusAsync();

            var json = JsonSerializer.Serialize(new
            {
                Servers = statuses.Select(s => new
                {
                    s.ServerId,
                    s.EndpointHost,
                    s.EndpointPort,
                    s.ServerPublicKey,
                    s.Seeded,
                    s.KeyPresent,
                    s.UpToDate,
                    s.Differences,
                }),
                RestoredCount = restoreResult?.RestoredCount,
                MeasuredAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }, RouteTestJsonOptions);
            await ExecuteScriptSafelyAsync($"window.setGpnDefaultsStatus?.({json});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-defaults-status push failed", ex);
        }
    }

    /// <summary>
    /// Kullanıcının Sunucu Yönetimi ekranına yapıştırdığı WireGuard .conf metnini
    /// parse edip gpn_servers'a ekler (private key DPAPI ile şifrelenerek saklanır).
    /// Her [Peer] bloğu ayrı bir sunucu satırı olur; aynı uç nokta yeniden içe
    /// aktarılırsa satır çoğalmaz (upsert).
    /// </summary>
    private async Task ImportGpnServersAsync(string confText)
    {
        var peers = WireguardFmt.ResolveConfig(confText);
        if (peers is null || peers.Count == 0)
        {
            await NotifyGpnServersOpAsync("GPN .conf çözümlenemedi — [Interface]/[Peer] bloğu bekleniyordu.");
            return;
        }

        var imported = 0;
        foreach (var peer in peers)
        {
            if (peer.ConfigType != EConfigType.WireGuard)
            {
                continue;
            }
            imported += await WireGuardServerCatalog.UpsertFromProfileAsync(peer);
        }

        await PushGpnServersAsync();
        await NotifyGpnServersOpAsync(imported > 0
            ? $"{imported} GPN sunucusu içe aktarıldı."
            : "GPN .conf içe aktarılamadı — uç nokta/anahtar eksik.");
    }

    /// <summary>Sunucu Yönetimi ekranından tek bir GPN sunucusunu siler.</summary>
    private async Task DeleteGpnServerAsync(string serverId)
    {
        var removed = await WireGuardServerCatalog.RemoveAsync(serverId);
        await PushGpnServersAsync();
        await NotifyGpnServersOpAsync(removed > 0
            ? "GPN sunucusu silindi."
            : "GPN sunucusu silinemedi.");
    }

    /// <summary>Sunucunun IsEnabled bayrağını değiştirir (otomatik seçimde adaylığı kapatır/açar).</summary>
    private async Task ToggleGpnServerAsync(string serverId, bool enabled)
    {
        var items = await WireGuardServerCatalog.GetItemsAsync();
        var item = (items ?? []).FirstOrDefault(i => i.ServerId == serverId);
        if (item is null)
        {
            await NotifyGpnServersOpAsync("GPN sunucusu bulunamadı.");
            return;
        }

        item.IsEnabled = enabled;
        item.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await WireGuardServerCatalog.UpsertAsync(item);
        await PushGpnServersAsync();
        await NotifyGpnServersOpAsync(enabled ? "GPN sunucusu etkinleştirildi." : "GPN sunucusu devre dışı bırakıldı.");
    }

    private async Task NotifyGpnServersOpAsync(string message)
    {
        try
        {
            await ExecuteScriptSafelyAsync($"window.gpnServersNotify?.({JsonSerializer.Serialize(message)});");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN gpn-servers notify failed", ex);
        }
    }

    /// <summary>
    /// Sunucu Yönetimi ekranından WPF ekleme/düzenleme penceresini açar.
    /// <paramref name="existingServerId"/> verilirse o satır düzenlenir (özel anahtar
    /// DPAPI'den çözülür, alan boş bırakılırsa mevcut anahtar korunur). Kayıt başarılıysa
    /// katalog zaten güncellenmiştir; dashboard listesi tazelenir.
    /// </summary>
    private async Task ShowGpnServerEditDialogAsync(string? existingServerId)
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

    /// <summary>
    /// Runs the rule-drift health check on a background thread and pushes the verdict
    /// into the dashboard. The check rebuilds the sing-box config the active core would
    /// use right now and compares its route rules against the config the running core
    /// loaded — a mismatch means the tunnel is enforcing stale rules (e.g. routing edited
    /// in the settings without a reload). No traffic is sent.
    /// </summary>
    private async Task PushRuleDriftAsync(bool force = false)
    {
        if (!_webViewReady)
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

    private async Task PushProcessCatalogAsync()
    {
        if (!_webViewReady)
        {
            return;
        }

        try
        {
            var processes = await Task.Run(() => _processCatalogService.GetRunningProcesses(_webViewLifetime.Token));
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
        catch (OperationCanceledException) when (_webViewLifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN process catalog push failed", ex);
        }
    }

    private async Task PushMonitorSnapshotAsync(bool force = false)
    {
        if (!_webViewReady || ViewModel?.ConnectionViewModel is not { } connectionViewModel)
        {
            return;
        }

        // The connection/split tables only exist on the Performance and Game Boost
        // views. The 2 s poll skips serialization and DOM re-rendering while the user
        // is elsewhere; entering those views requests a snapshot explicitly.
        if (!force && _activeView is not ("perf" or "boost"))
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
        var transport = ReadTransport();
        var flushedCount = TunLifecycleManager.DrainFlushCount();

        // Compute the routing-engine mode the core is running under.
        //  - gpn   : GPN Game Tunnel — stripped v2rayN baggage, process_name only
        //  - global: Global VPN — full legacy clash_mode / geoip / hosts chain
        //  - proxy : Proxy capture — no TUN, OS proxy routes all traffic
        //  - none  : Disconnected or undefined
        string routingMode;
        if (!ReadActualConnectionState())
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

        var payload = new
        {
            updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            connected = ReadActualConnectionState(),
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
            connections,
            apps,
            traffic,
        };

        var json = JsonSerializer.Serialize(payload);
        await ExecuteScriptSafelyAsync($"window.updateMonitorSnapshot({json});");
    }

    /// <summary>
    /// Switches the active AoGPN server from the dashboard node list. Reuses the
    /// same persistence + core reload path as the native servers view, then pushes
    /// the switched profile and refreshed selection back to the renderer.
    /// </summary>
    private async Task SelectNodeAsync(string indexId)
    {
        var profilesViewModel = ViewModel?.ProfilesViewModel;
        if (profilesViewModel is null)
        {
            await AcknowledgeNodeSwitchAsync(succeeded: false, indexId);
            return;
        }

        var succeeded = false;
        try
        {
            await profilesViewModel.SetDefaultServer(indexId);
            // SetDefaultServer can return silently (unknown id, same server); the
            // persisted IndexId is the authoritative proof the switch took effect.
            succeeded = AppManager.Instance.Config.IndexId == indexId;
            if (succeeded)
            {
                // Feed the "Recently used" node sort.
                ProfileExManager.Instance.TouchLastUsed(indexId);
                await ProfileExManager.Instance.SaveTo();

                // While the proxy-only core is up (no tunnel), re-run it on the newly
                // selected node so the system proxy follows the node picker.
                if (_proxyOnlyService.IsRunning)
                {
                    try
                    {
                        await _proxyOnlyService.RestartOnNodeChangeAsync(AppManager.Instance.Config);
                        await PushSystemProxyStateAsync(force: true);
                    }
                    catch (Exception ex)
                    {
                        Logging.SaveLog("AoGPN proxy-only node switch failed", ex);
                    }
                }

                UpdateTrayStatus();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("WebView2 server switch failed", ex);
        }

        // Always publish the effective profile and selection, then acknowledge so
        // the renderer can clear its "switching…" state or roll the switch back.
        await PushNodeInfoAsync(force: true);
        await PushNodeListAsync();
        await AcknowledgeNodeSwitchAsync(succeeded, indexId);
    }

    private async Task AcknowledgeNodeSwitchAsync(bool succeeded, string indexId)
    {
        await ExecuteScriptSafelyAsync(
            $"window.setNodeSwitchResult({JsonSerializer.Serialize(succeeded)}, {JsonSerializer.Serialize(indexId)});");
    }

    /// <summary>
    /// Loads the profile entities for the given ids; unknown or unreadable ids are
    /// skipped so a stale renderer selection can never crash the operation.
    /// </summary>
    private async Task<List<ProfileItem>> LoadProfilesByIdsAsync(IReadOnlyCollection<string> indexIds)
    {
        var profiles = new List<ProfileItem>();
        foreach (var id in indexIds)
        {
            if (id.IsNullOrEmpty())
            {
                continue;
            }

            try
            {
                var item = await AppManager.Instance.GetProfileItem(id);
                if (item is not null)
                {
                    profiles.Add(item);
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog("AoGPN node lookup failed", ex);
            }
        }
        return profiles;
    }

    /// <summary>
    /// Copies the share links (vless://, vmess://, ...) of the selected nodes to the
    /// clipboard, mirroring the native servers view's Ctrl+C (Export2ShareUrl). An
    /// empty selection copies the active profile.
    /// </summary>
    private async Task CopyNodesAsync(string[] indexIds)
    {
        var ids = indexIds.Length > 0 ? indexIds : new[] { AppManager.Instance.Config.IndexId };
        var profiles = await LoadProfilesByIdsAsync(ids);
        if (profiles.Count == 0)
        {
            return;
        }

        var sb = new StringBuilder();
        foreach (var item in profiles)
        {
            var url = FmtHandler.GetShareUri(item);
            if (url.IsNullOrEmpty())
            {
                continue;
            }
            sb.AppendLine(url);
        }

        if (sb.Length == 0)
        {
            await NotifyNodesOpAsync("Selected nodes have no share link");
            return;
        }

        WindowsUtils.SetClipboardData(sb.ToString());
        await NotifyNodesOpAsync($"Copied {profiles.Count} node share link(s) to clipboard");
    }

    /// <summary>
    /// Imports nodes from the clipboard into the current group, mirroring the native
    /// Ctrl+V (AddServerViaClipboard), then republishes the refreshed node list.
    /// </summary>
    private async Task PasteNodesAsync()
    {
        var clipboardData = WindowsUtils.GetClipboardData();
        if (clipboardData.IsNullOrEmpty())
        {
            await NotifyNodesOpAsync("Clipboard is empty");
            return;
        }

        try
        {
            var ret = await ConfigHandler.AddBatchServers(
                AppManager.Instance.Config, clipboardData, AppManager.Instance.Config.SubIndexId, false);
            Logging.SaveLog($"AoGPN clipboard paste imported {ret} node(s)");
            await PushNodeListAsync();
            await NotifyNodesOpAsync(ret > 0
                ? $"Imported {ret} node(s) from clipboard"
                : "No valid nodes found in clipboard");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN clipboard import failed", ex);
            await NotifyNodesOpAsync("Clipboard import failed");
        }
    }

    /// <summary>
    /// Deletes the given nodes through the same persistence path as the native view
    /// (ConfigHandler.RemoveServers), reloads the core if the active profile was
    /// among the removed ones, then republishes the list.
    /// </summary>
    private async Task DeleteNodesAsync(string[] indexIds)
    {
        var profiles = await LoadProfilesByIdsAsync(indexIds);
        if (profiles.Count == 0)
        {
            return;
        }

        try
        {
            var config = AppManager.Instance.Config;
            var removedActive = profiles.Exists(t => t.IndexId == config.IndexId);
            await ConfigHandler.RemoveServers(config, profiles);

            var profilesViewModel = ViewModel?.ProfilesViewModel;
            if (profilesViewModel is not null)
            {
                await profilesViewModel.RefreshServers();
                if (removedActive)
                {
                    profilesViewModel.ReloadRequested.Publish();
                }
            }

            await PushNodeInfoAsync(force: true);
            await PushNodeListAsync();
            await NotifyNodesOpAsync($"Deleted {profiles.Count} node(s)");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN node delete failed", ex);
            await NotifyNodesOpAsync("Delete failed");
        }
    }

    /// <summary>
    /// Runs the native real-ping test over the given nodes (empty array = the whole
    /// current group) and streams per-node results to the renderer. The same
    /// SpeedtestService the native servers view uses, so delays persist in
    /// ProfileEx and the "remove failed" cleanup matches native semantics.
    /// </summary>
    private async Task StartNodeSpeedtestAsync(string[] indexIds, string? testType = null, long requestedRunId = 0)
    {
        if (_nodeTestRunning)
        {
            // The flag can only be trusted while the service really has a run in
            // flight (e.g. a dashboard page reload can strand it as true). A
            // stuck flag must not permanently block new tests.
            if (_nodeSpeedtestService?.HasActiveRun == true)
            {
                await NotifyNodesOpAsync("A ping test is already running — press Stop to cancel it");
                return;
            }
            _nodeTestRunning = false;
        }

        List<ProfileItem> profiles;
        try
        {
            profiles = indexIds.Length > 0
                ? await LoadProfilesByIdsAsync(indexIds)
                : await AppManager.Instance.ProfileItems(AppManager.Instance.Config.SubIndexId) ?? [];
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN ping test profile load failed", ex);
            await NotifyNodesOpAsync("Ping test could not load the nodes");
            return;
        }

        // Skip non-testable profiles (custom configs, portless groups) the same
        // way SpeedtestService.GetClearItem does.
        var testable = profiles.Where(p => p.ConfigType != EConfigType.Custom
            && (p.ConfigType.IsComplexType() || p.Port > 0)).ToList();
        if (testable.Count == 0)
        {
            await NotifyNodesOpAsync("No testable nodes in the selection");
            return;
        }

        var normalizedTestType = testType is "udp" or "both" ? testType : "tcp";
        var actionType = normalizedTestType switch
        {
            "udp" => ESpeedActionType.UdpTest,
            "both" => ESpeedActionType.Mixedtest,
            _ => ESpeedActionType.Tcping,
        };

        _nodeTestRunning = true;
        var runId = requestedRunId > 0 ? requestedRunId : Interlocked.Increment(ref _nodeTestRunId);
        Interlocked.Exchange(ref _nodeTestRunId, runId);
        await ExecuteScriptSafelyAsync($"window.setNodeTestRunning(true, {runId});");
        Logging.SaveLog($"AoGPN ping test starting with {testable.Count} node(s), type={normalizedTestType}");

        _nodePingCancellation?.Cancel();
        _nodePingCancellation = new CancellationTokenSource();
        _nodeSpeedtestService ??= new SpeedtestService(AppManager.Instance.Config, result =>
        {
            // Never await the renderer from the speed-test worker thread:
            // CoreWebView2.ExecuteScriptAsync can deadlock when awaited off the UI
            // thread, which would stall the whole test run. Dispatch the DOM push
            // onto the UI thread and return immediately, mirroring how the native
            // servers view schedules speed-test updates on the main scheduler.
            if (result is null || _isClosing || !_webViewReady)
            {
                return Task.CompletedTask;
            }

            try
            {
                Dispatcher.InvokeAsync(() => PushNodeTestResultAsync(result));
            }
            catch
            {
                // The dispatcher is shutting down; there is nothing left to push.
            }

            return Task.CompletedTask;
        });

        // A previous run that was interrupted is stopped before starting fresh.
        _nodeSpeedtestService.ExitLoop();
        _nodeSpeedtestService.RunLoop(actionType, testable, _nodePingCancellation.Token);
    }

    /// <summary>
    /// Pushes one speed-test result to the renderer on the UI thread. An empty
    /// IndexId marks the run as finished/stopped and clears the running state.
    /// </summary>
    private async Task PushNodeTestResultAsync(SpeedTestResult result)
    {
        if (result is null)
        {
            return;
        }

        var runId = Volatile.Read(ref _nodeTestRunId);
        if (result.IndexId.IsNullOrEmpty())
        {
            _nodeTestRunning = false;
        }

        await ExecuteScriptSafelyAsync(
            $"window.updateNodeTest({JsonSerializer.Serialize(result.IndexId)}, {JsonSerializer.Serialize(result.Delay)}, {runId});");
    }

    /// <summary>Stops a running ping test; the service reports the stop back to the renderer.</summary>
    private void StopNodeSpeedtest()
    {
        Interlocked.Increment(ref _nodeTestRunId);
        _nodePingCancellation?.Cancel();
        _nodeSpeedtestService?.ExitLoop();
        _nodeTestRunning = false;
    }

    /// <summary>
    /// Moves the given nodes into the dashboard's Disabled section (a config-level
    /// flag, not a DB move), hides them from the main list, and persists the change.
    /// </summary>
    private async Task DisableNodesAsync(string[] indexIds)
    {
        var profiles = await LoadProfilesByIdsAsync(indexIds);
        if (profiles.Count == 0)
        {
            return;
        }

        try
        {
            var config = AppManager.Instance.Config;
            config.DisabledIndexIds ??= [];
            var added = 0;
            foreach (var profile in profiles)
            {
                if (!config.DisabledIndexIds.Contains(profile.IndexId))
                {
                    config.DisabledIndexIds.Add(profile.IndexId);
                    added++;
                }
            }
            if (added > 0)
            {
                await ConfigHandler.SaveConfig(config);
            }

            await PushNodeListAsync();
            await NotifyNodesOpAsync($"{added} node(s) moved to Disabled");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN node disable failed", ex);
            await NotifyNodesOpAsync("Disable failed");
        }
    }

    /// <summary>Moves the given nodes back from the Disabled section into the main list.</summary>
    private async Task RestoreNodesAsync(string[] indexIds)
    {
        try
        {
            var config = AppManager.Instance.Config;
            if (config.DisabledIndexIds is null || config.DisabledIndexIds.Count == 0)
            {
                return;
            }

            var restored = 0;
            foreach (var id in indexIds)
            {
                if (config.DisabledIndexIds.Remove(id))
                {
                    restored++;
                }
            }
            if (restored == 0)
            {
                return;
            }

            await ConfigHandler.SaveConfig(config);
            await PushNodeListAsync();
            await NotifyNodesOpAsync($"{restored} node(s) restored");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN node restore failed", ex);
            await NotifyNodesOpAsync("Restore failed");
        }
    }

    /// <summary>
    /// Finds the nodes in the current group whose last real-ping result was a
    /// failure (ProfileEx delay == -1) and either deletes them or moves them to
    /// the Disabled section, mirroring native RemoveInvalidServerResult.
    /// </summary>
    private async Task CleanupFailedNodesAsync(string target)
    {
        try
        {
            var config = AppManager.Instance.Config;
            var lstModel = await AppManager.Instance.ProfileModels(config.SubIndexId, "");
            if (lstModel is null || lstModel.Count == 0)
            {
                return;
            }

            var lstProfileExs = await ProfileExManager.Instance.GetProfileExs();
            var failedIds = lstModel
                .Where(t => !t.ConfigType.IsComplexType()
                    && lstProfileExs.Any(e => e.IndexId == t.IndexId && e.Delay == -1))
                .Select(t => t.IndexId)
                .ToHashSet();
            if (failedIds.Count == 0)
            {
                await NotifyNodesOpAsync("No failed nodes found — run a ping test first");
                return;
            }

            var lstProfile = await AppManager.Instance.ProfileItems(config.SubIndexId) ?? [];
            var failed = lstProfile.Where(p => failedIds.Contains(p.IndexId)).ToList();
            if (failed.Count == 0)
            {
                return;
            }

            if (target == "disable")
            {
                config.DisabledIndexIds ??= [];
                var added = 0;
                foreach (var item in failed)
                {
                    if (!config.DisabledIndexIds.Contains(item.IndexId))
                    {
                        config.DisabledIndexIds.Add(item.IndexId);
                        added++;
                    }
                }
                if (added > 0)
                {
                    await ConfigHandler.SaveConfig(config);
                }
                await PushNodeListAsync();
                await NotifyNodesOpAsync($"{added} failed node(s) moved to Disabled");
                return;
            }

            var removedActive = failed.Exists(t => t.IndexId == config.IndexId);
            await ConfigHandler.RemoveServers(config, failed);

            var profilesViewModel = ViewModel?.ProfilesViewModel;
            if (profilesViewModel is not null)
            {
                await profilesViewModel.RefreshServers();
                if (removedActive)
                {
                    profilesViewModel.ReloadRequested.Publish();
                }
            }

            await PushNodeInfoAsync(force: true);
            await PushNodeListAsync();
            await NotifyNodesOpAsync($"{failed.Count} failed node(s) deleted");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN failed-node cleanup failed", ex);
            await NotifyNodesOpAsync("Cleanup failed");
        }
    }

    /// <summary>
    /// Removes duplicate profiles from the current group using the same property-
    /// based comparison as the native servers view, keeping only one of each.
    /// </summary>
    private async Task DedupNodesAsync()
    {
        try
        {
            var config = AppManager.Instance.Config;
            var tuple = await ConfigHandler.DedupServerList(config, config.SubIndexId);
            if (tuple.Item1 > 0)
            {
                await PushNodeInfoAsync(force: true);
                await PushNodeListAsync();
                await NotifyNodesOpAsync(
                    $"Duplicates removed: {tuple.Item1 - tuple.Item2} of {tuple.Item1} kept {tuple.Item2}");
            }
            else
            {
                await NotifyNodesOpAsync("No duplicate nodes found");
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN node dedup failed", ex);
            await NotifyNodesOpAsync("Deduplicate failed");
        }
    }

    /// <summary>Pushes the node-pool link list to the dashboard Nodes view.</summary>
    private async Task PushNodePoolAsync()
    {
        if (!_webViewReady)
        {
            return;
        }

        var links = AppManager.Instance.Config.NodePoolLinks ?? [];
        await ExecuteScriptSafelyAsync(
            $"window.updateNodePool({JsonSerializer.Serialize(links)});");
    }

    /// <summary>Adds a raw .txt / subscription URL to the node pool.</summary>
    private async Task AddNodePoolLinkAsync(string url)
    {
        url = url.Trim();
        if (!url.StartsWith(Global.HttpsProtocol, StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith(Global.HttpProtocol, StringComparison.OrdinalIgnoreCase))
        {
            await NotifyNodesOpAsync("Invalid link — must start with http:// or https://");
            return;
        }

        var config = AppManager.Instance.Config;
        config.NodePoolLinks ??= [];
        if (!config.NodePoolLinks.Contains(url, StringComparer.OrdinalIgnoreCase))
        {
            config.NodePoolLinks.Add(url);
            await ConfigSaveQueue.SaveAndWaitAsync(config);
        }
        await PushNodePoolAsync();
        await NotifyNodesOpAsync("Link added to pool");
    }

    /// <summary>Replaces a pooled URL with an edited one (same validation as add).</summary>
    private async Task EditNodePoolLinkAsync(string url, string newUrl)
    {
        url = url.Trim();
        newUrl = newUrl.Trim();
        if (!newUrl.StartsWith(Global.HttpsProtocol, StringComparison.OrdinalIgnoreCase)
            && !newUrl.StartsWith(Global.HttpProtocol, StringComparison.OrdinalIgnoreCase))
        {
            await NotifyNodesOpAsync("Invalid link — must start with http:// or https://");
            return;
        }

        var config = AppManager.Instance.Config;
        config.NodePoolLinks ??= [];
        if (!config.NodePoolLinks.Contains(url, StringComparer.OrdinalIgnoreCase))
        {
            await NotifyNodesOpAsync("Link not found in pool");
            return;
        }
        if (config.NodePoolLinks.Any(l => !l.Equals(url, StringComparison.OrdinalIgnoreCase)
                                          && l.Equals(newUrl, StringComparison.OrdinalIgnoreCase)))
        {
            await NotifyNodesOpAsync("Link already in pool");
            return;
        }

        var idx = config.NodePoolLinks.FindIndex(l => l.Equals(url, StringComparison.OrdinalIgnoreCase));
        config.NodePoolLinks[idx] = newUrl;
        await ConfigSaveQueue.SaveAndWaitAsync(config);
        await PushNodePoolAsync();
        await NotifyNodesOpAsync("Link updated");
    }

    /// <summary>Removes a URL from the node pool.</summary>
    private async Task RemoveNodePoolLinkAsync(string url)
    {
        var config = AppManager.Instance.Config;
        config.NodePoolLinks ??= [];
        config.NodePoolLinks.RemoveAll(l => l.Equals(url.Trim(), StringComparison.OrdinalIgnoreCase));
        await ConfigSaveQueue.SaveAndWaitAsync(config);
        await PushNodePoolAsync();
        await NotifyNodesOpAsync("Link removed from pool");
    }

    /// <summary>
    /// Downloads every link in the node pool (plain .txt, base64 or subscription
    /// endpoints) and imports the shared nodes into the current group without
    /// touching existing entries. Reports per-link progress through the Nodes-view
    /// toast, then republishes the node list.
    /// </summary>
    private async Task FetchNodePoolAsync()
    {
        var config = AppManager.Instance.Config;
        var links = config.NodePoolLinks ?? [];
        if (links.Count == 0)
        {
            await NotifyNodesOpAsync("Pool is empty — add links first");
            return;
        }

        await NotifyNodesOpAsync($"Downloading nodes from {links.Count} pool link(s)…");
        var total = 0;
        var totalLinks = 0;
        foreach (var link in links)
        {
            var url = Utils.GetPunycode(link.Trim());
            if (url.IsNullOrEmpty())
            {
                continue;
            }

            var download = new DownloadService();
            try
            {
                var content = await download.TryDownloadString(url, false, "");
                // Retry through the proxy when a direct fetch comes back empty.
                if (content.IsNullOrEmpty())
                {
                    content = await download.TryDownloadString(url, true, "");
                }
                if (content.IsNullOrEmpty())
                {
                    await NotifyNodesOpAsync($"{link} — no content");
                    continue;
                }

                content = NormalizeNodePoolContent(content);
                var ret = await ConfigHandler.AddBatchServers(config, content, "", false);
                if (ret > 0)
                {
                    total += ret;
                    totalLinks++;
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog($"NodePool fetch failed: {link}", ex);
                await NotifyNodesOpAsync($"{link} — failed");
            }
        }

        await PushNodeInfoAsync(force: true);
        await PushNodeListAsync();
        await PushNodePoolAsync();
        await NotifyNodesOpAsync(
            total > 0
                ? $"{total} new nodes imported from {totalLinks} pool link(s)"
                : "No new nodes found in the pool");
    }

    private static string NormalizeNodePoolContent(string content)
    {
        var value = content.Trim();
        if (value.Length == 0) return value;

        // Subscription feeds are commonly base64-wrapped. Decode only when the
        // decoded text looks like a node URI or a JSON profile document.
        var compact = string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
        try
        {
            var padded = compact.PadRight(compact.Length + (4 - compact.Length % 4) % 4, '=');
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            if (decoded.Contains("://", StringComparison.Ordinal)
                || decoded.Contains("{\"", StringComparison.Ordinal)
                || decoded.Contains("outbounds", StringComparison.OrdinalIgnoreCase))
            {
                value = decoded;
            }
        }
        catch (FormatException)
        {
            // Plain text feeds are expected and need no decoding.
        }

        // Accept JSON arrays/objects emitted by several public aggregators by
        // extracting common URI lines; AddBatchServers handles the URI formats.
        if (value.TrimStart().StartsWith('{') || value.TrimStart().StartsWith('['))
        {
            try
            {
                using var document = JsonDocument.Parse(value);
                var uris = new List<string>();
                CollectNodeUris(document.RootElement, uris);
                value = string.Join(Environment.NewLine, uris);
            }
            catch (JsonException)
            {
                // Let the existing batch parser report unsupported content.
            }
        }

        return string.Join(Environment.NewLine, value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => IsNodeUri(line) || line.Contains("vless://", StringComparison.OrdinalIgnoreCase)
                || line.Contains("vmess://", StringComparison.OrdinalIgnoreCase)
                || line.Contains("ss://", StringComparison.OrdinalIgnoreCase)
                || line.Contains("trojan://", StringComparison.OrdinalIgnoreCase)
                || line.Contains("hysteria", StringComparison.OrdinalIgnoreCase)
                || line.Contains("tuic://", StringComparison.OrdinalIgnoreCase)));
    }

    private static void CollectNodeUris(JsonElement element, List<string> uris)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (IsNodeUri(value)) uris.Add(value!);
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray()) CollectNodeUris(child, uris);
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject()) CollectNodeUris(property.Value, uris);
        }
    }

    private static bool IsNodeUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var line = value.Trim();
        return line.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("hysteria://", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("tuic://", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("socks://", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Flips the favorite star for a node and republishes the list.</summary>
    private async Task ToggleNodeFavAsync(string indexId)
    {
        var current = ProfileExManager.Instance.GetFav(indexId);
        ProfileExManager.Instance.SetFav(indexId, !current);
        await ProfileExManager.Instance.SaveTo();
        await PushNodeListAsync();
        await NotifyNodesOpAsync(!current ? "Added to favorites" : "Removed from favorites");
    }

    /// <summary>Shows a transient toast in the dashboard's Nodes view.</summary>
    private async Task NotifyNodesOpAsync(string message)
    {
        if (!_webViewReady)
        {
            return;
        }
        await ExecuteScriptSafelyAsync($"window.notifyNodes({JsonSerializer.Serialize(message)});");
    }

    /// <summary>
    /// Streams the active subscription group's real profiles to the renderer in
    /// chunks (large subscriptions would exceed a single script payload), then
    /// finalizes with the active profile id so the Nodes view can render.
    /// </summary>
    private async Task PushNodeListAsync()
    {
        if (!_webViewReady)
        {
            return;
        }

        List<ProfileItemModel>? profiles;
        try
        {
            profiles = await AppManager.Instance.ProfileModels(AppManager.Instance.Config.SubIndexId, "");
        }
        catch (Exception ex)
        {
            Logging.SaveLog("AoGPN node list lookup failed", ex);
            return;
        }

        if (profiles is null || profiles.Count == 0)
        {
            return;
        }

        var config = AppManager.Instance.Config;
        var activeIndexId = config.IndexId;
        var disabledIds = new HashSet<string>(config.DisabledIndexIds ?? []);

        // Favorites and last-used stamps come from the profile extension table so
        // the dashboard can offer country / favorite / recently-used sorting.
        var profileExs = await ProfileExManager.Instance.GetProfileExs();
        var exByIndex = new Dictionary<string, ProfileExItem>(StringComparer.Ordinal);
        if (profileExs is not null)
        {
            foreach (var ex in profileExs)
            {
                if (ex?.IndexId is { Length: > 0 } && !exByIndex.ContainsKey(ex.IndexId))
                {
                    exByIndex[ex.IndexId] = ex;
                }
            }
        }

        var projectNode = (ProfileItemModel p) =>
        {
            var ex = exByIndex.TryGetValue(p.IndexId, out var foundEx) ? foundEx : null;
            return new
            {
                indexId = p.IndexId,
                name = p.Remarks ?? string.Empty,
                address = p.Address ?? string.Empty,
                port = p.Port,
                protocol = p.ConfigType.ToString(),
                sub = p.SubRemarks ?? string.Empty,
                delay = p.Delay > 0 ? p.Delay : 0,
                active = p.IndexId == activeIndexId,
                // Prefer the explicit remark code, then resolve the address IP via
                // GeoIP so plain-IP nodes still sort by country.
                country = ResolveNodeCountry(p.Remarks, p.Address),
                fav = ex?.IsFav ?? false,
                lastUsed = ex?.LastUsed ?? 0,
            };
        };

        // Disabled nodes are hidden from the main list and pushed separately so
        // the renderer can show them in their own section with restore/delete.
        var nodes = profiles.Where(p => !disabledIds.Contains(p.IndexId)).Select(projectNode).ToList();
        var disabledNodes = profiles.Where(p => disabledIds.Contains(p.IndexId)).Select(projectNode).ToList();

        const int chunkSize = 150;
        for (var offset = 0; offset < nodes.Count; offset += chunkSize)
        {
            var chunk = nodes.Skip(offset).Take(chunkSize).ToList();
            var chunkJson = JsonSerializer.Serialize(chunk);
            await ExecuteScriptSafelyAsync($"window.updateNodeListAppend({chunkJson});");
        }

        var disabledJson = JsonSerializer.Serialize(disabledNodes);
        await ExecuteScriptSafelyAsync($"window.updateDisabledNodes({disabledJson});");

        var activeJson = JsonSerializer.Serialize(activeIndexId);
        await ExecuteScriptSafelyAsync($"window.updateNodeListDone({activeJson});");
    }

    /// <summary>
    /// Pushes the full AoGPN option set (mirroring OptionSettingViewModel) plus the
    /// Global.* combo lists to the dashboard Settings view. Called on navigation
    /// completion and on demand via get_settings.
    /// </summary>
    private async Task PushSettingsAsync()
    {
        if (!_webViewReady)
        {
            return;
        }

        var config = AppManager.Instance.Config;
        var inbound = config.Inbound.First();
        var coreTypes = new Dictionary<int, string>();
        foreach (var item in config.CoreTypeItem ?? [])
        {
            coreTypes[(int)item.ConfigType] = item.CoreType.ToString();
        }

        var payload = new
        {
            core = new
            {
                localPort = inbound.LocalPort,
                secondLocalPortEnabled = inbound.SecondLocalPortEnabled,
                udpEnabled = inbound.UdpEnabled,
                sniffingEnabled = inbound.SniffingEnabled,
                destOverride = inbound.DestOverride ?? [],
                routeOnly = inbound.RouteOnly,
                allowLANConn = inbound.AllowLANConn,
                newPort4LAN = inbound.NewPort4LAN,
                user = inbound.User ?? string.Empty,
                pass = inbound.Pass ?? string.Empty,
                logEnabled = config.CoreBasicItem.LogEnabled,
                loglevel = config.CoreBasicItem.Loglevel ?? string.Empty,
                defFingerprint = config.CoreBasicItem.DefFingerprint ?? string.Empty,
                defUserAgent = config.CoreBasicItem.DefUserAgent ?? string.Empty,
                sendThrough = config.CoreBasicItem.SendThrough ?? string.Empty,
                bindInterface = config.CoreBasicItem.BindInterface ?? string.Empty,
                mux4SboxProtocol = config.Mux4SboxItem.Protocol ?? string.Empty,
                enableCacheFile4Sbox = config.CoreBasicItem.EnableCacheFile4Sbox,
                hyUpMbps = config.HysteriaItem.UpMbps,
                hyDownMbps = config.HysteriaItem.DownMbps,
                enableFragment = config.CoreBasicItem.EnableFragment,
                enableFinalFragment = config.CoreBasicItem.EnableFinalFragment,
                fragmentPackets = config.Fragment4RayItem?.Packets ?? string.Empty,
                fragmentLengths = Utils.List2String(config.Fragment4RayItem?.Lengths),
                fragmentDelays = Utils.List2String(config.Fragment4RayItem?.Delays),
                fragmentMaxSplit = config.Fragment4RayItem?.MaxSplit ?? string.Empty,
            },
            general = new
            {
                autoRun = config.GuiItem.AutoRun,
                enableStatistics = config.GuiItem.EnableStatistics,
                displayRealTimeSpeed = config.GuiItem.DisplayRealTimeSpeed,
                keepOlderDedupl = config.GuiItem.KeepOlderDedupl,
                enableAutoAdjustMainLvColWidth = config.UiItem.EnableAutoAdjustMainLvColWidth,
                autoHideStartup = config.UiItem.AutoHideStartup,
                hide2TrayWhenClose = config.UiItem.Hide2TrayWhenClose,
                minimize2Tray = config.UiItem.Minimize2Tray,
                enableDragDropSort = config.UiItem.EnableDragDropSort,
                doubleClick2Activate = config.UiItem.DoubleClick2Activate,
                enableHWA = config.GuiItem.EnableHWA,
                verboseLogEnabled = config.GuiItem.EnableVerboseLog,
                effectsMode = NormalizeEffectsMode(config.GuiItem.EffectsMode),
                reduceEffects = config.GuiItem.ReduceEffects,
                rootCertProvider = config.GuiItem.RootCertProvider ?? string.Empty,
                autoUpdateInterval = config.GuiItem.AutoUpdateInterval,
                trayMenuServersLimit = config.GuiItem.TrayMenuServersLimit,
                currentFontFamily = config.UiItem.CurrentFontFamily ?? string.Empty,
                mixedConcurrencyCount = config.SpeedTestItem.MixedConcurrencyCount,
                speedTestTimeout = config.SpeedTestItem.SpeedTestTimeout,
                speedTestUrl = config.SpeedTestItem.SpeedTestUrl ?? string.Empty,
                speedPingTestUrl = config.SpeedTestItem.SpeedPingTestUrl ?? string.Empty,
                udpTestTarget = config.SpeedTestItem.UdpTestTarget ?? string.Empty,
                ipapiUrl = config.SpeedTestItem.IPAPIUrl ?? string.Empty,
                subConvertUrl = config.ConstItem.SubConvertUrl ?? string.Empty,
                geoFileSourceUrl = config.ConstItem.GeoSourceUrl ?? string.Empty,
                srsFileSourceUrl = config.ConstItem.SrsSourceUrl ?? string.Empty,
                routingRulesSourceUrl = config.ConstItem.RouteRulesTemplateSourceUrl ?? string.Empty,
            },
            connection = new
            {
                mode = config.ConnectionItem.Mode switch
                {
                    SplitTunnelViewModel.ModeManual => "manual",
                    SplitTunnelViewModel.ModeVpn => "vpn",
                    _ => "off",
                },
                transport = ReadTransport(),
                protocolPreference = ConnectionProtocolPreference.Normalize(config.ConnectionItem.ProtocolPreference),
                invertManualRouting = config.ConnectionItem.InvertManualRouting,
                autoConnectOnGameStart = config.ConnectionItem.AutoConnectOnGameStart,
                autoReconnectEnabled = config.ConnectionItem.AutoReconnectEnabled,
                autoReconnectMaxAttempts = config.ConnectionItem.AutoReconnectMaxAttempts,
                gpnEnableRecoveryWatch = config.GuiItem.GpnEnableRecoveryWatch,
                gpnEnableWarpAutoRecover = config.GuiItem.GpnEnableWarpAutoRecover,
                gpnEnableFailover = config.GuiItem.GpnEnableFailover,
            },
            systemProxy = new
            {
                sysProxyType = (int)config.SystemProxyItem.SysProxyType,
                effectiveSysProxyType = (int)SystemProxyPolicy.ResolveEffectiveType(config),
                connectionOwnsProxy = SystemProxyPolicy.ConnectionNeedsSystemProxy(config),
                notProxyLocalAddress = config.SystemProxyItem.NotProxyLocalAddress,
                systemProxyAdvancedProtocol = config.SystemProxyItem.SystemProxyAdvancedProtocol ?? string.Empty,
                systemProxyExceptions = config.SystemProxyItem.SystemProxyExceptions ?? string.Empty,
                customSystemProxyPacPath = config.SystemProxyItem.CustomSystemProxyPacPath ?? string.Empty,
                customSystemProxyScriptPath = config.SystemProxyItem.CustomSystemProxyScriptPath ?? string.Empty,
            },
            tun = new
            {
                enableTun = config.TunModeItem.EnableTun,
                tunAutoRoute = config.TunModeItem.AutoRoute,
                tunStrictRoute = config.TunModeItem.StrictRoute,
                tunStack = config.TunModeItem.Stack ?? string.Empty,
                tunMtu = config.TunModeItem.Mtu,
                tunEnableIPv6Address = config.TunModeItem.EnableIPv6Address,
                tunIcmpRouting = config.TunModeItem.IcmpRouting ?? string.Empty,
                tunEnableLegacyProtect = config.TunModeItem.EnableLegacyProtect,
                tunRouteExcludeAddress = Utils.List2String(config.TunModeItem.RouteExcludeAddress, true),
                tunIPv4Address = config.TunModeItem.IPv4Address ?? string.Empty,
                tunIPv6Address = config.TunModeItem.IPv6Address ?? string.Empty,
            },
            coreType = new
            {
                coreType1 = coreTypes.GetValueOrDefault(1, string.Empty),
                coreType2 = coreTypes.GetValueOrDefault(2, string.Empty),
                coreType3 = coreTypes.GetValueOrDefault(3, string.Empty),
                coreType4 = coreTypes.GetValueOrDefault(4, string.Empty),
                coreType5 = coreTypes.GetValueOrDefault(5, string.Empty),
                coreType6 = coreTypes.GetValueOrDefault(6, string.Empty),
                coreType7 = coreTypes.GetValueOrDefault(7, string.Empty),
                coreType9 = coreTypes.GetValueOrDefault(9, string.Empty),
            },
            options = new
            {
                destOverrideProtocols = Global.destOverrideProtocols,
                logLevels = Global.LogLevels,
                fingerprints = Global.Fingerprints,
                userAgents = Global.UserAgent,
                singboxMuxs = Global.SingboxMuxs,
                tunMtus = Global.TunMtus.Select(t => t.ToString()).ToList(),
                tunStacks = Global.TunStacks,
                tunIcmpRoutingPolicies = Global.TunIcmpRoutingPolicies,
                tunIPv4Addresses = Global.TunIPv4Address,
                tunIPv6Addresses = Global.TunIPv6Address,
                fragmentPacketsOptions = Global.FragmentPacketsOptions,
                coreTypes = Global.CoreTypes,
                speedTestUrls = Global.SpeedTestUrls,
                speedPingTestUrls = Global.SpeedPingTestUrls,
                udpTestTargets = Global.UdpTestTargets,
                subConvertUrls = Global.SubConvertUrls,
                geoFilesSources = Global.GeoFilesSources,
                singboxRulesetSources = Global.SingboxRulesetSources,
                routingRulesSources = Global.RoutingRulesSources,
                ipapiUrls = Global.IPAPIUrls,
                rootCertProviders = Global.RootCertProviders,
                ieProxyProtocols = Global.IEProxyProtocols,
                mixedConcurrencyCounts = Enumerable.Range(2, 7).Select(i => i.ToString()).ToList(),
                speedTestTimeouts = Enumerable.Range(2, 5).Select(i => (i * 5).ToString()).ToList(),
            },
            platform = new
            {
                isWindows = Utils.IsWindows(),
                isLinux = Utils.IsLinux(),
                isMacOS = Utils.IsMacOS(),
                isAdmin = Utils.IsAdministrator(),
            },
        };

        var json = JsonSerializer.Serialize(payload);
        await ExecuteScriptSafelyAsync($"window.applySettings({json});");
        await PushSystemProxyStateAsync(force: true);
    }

    /// <summary>
    /// Applies the dashboard Settings form to the config using the same validation and
    /// persistence flow as the native OptionSettingViewModel, then performs the system
    /// proxy / TUN side effects the native status bar would do on change.
    /// </summary>
    private async Task SaveSettingsAsync(JsonElement root)
    {
        try
        {
            await SaveSettingsCoreAsync(root);
        }
        catch (Exception ex)
        {
            // Ack the failure so the renderer can re-enable its Save button; a save
            // that silently dies would leave the form stuck in the saving state.
            Logging.SaveLog("AoGPN settings save failed", ex);
            await NotifySettingsSaveAsync(false, "Failed to save settings");
        }
    }

    private async Task SaveSettingsCoreAsync(JsonElement root)
    {
        if (!root.TryGetProperty("settings", out var settings)
            || settings.ValueKind != JsonValueKind.Object)
        {
            await NotifySettingsSaveAsync(false, "Invalid settings payload");
            return;
        }

        var config = AppManager.Instance.Config;

        // Local SOCKS port validation mirrors OptionSettingViewModel.SaveSettingAsync.
        var localPort = GetSettingsInt(settings, "localPort", 0);
        if (localPort <= 0 || localPort >= Global.MaxPort)
        {
            await NotifySettingsSaveAsync(false, "Fill in the local listening port");
            return;
        }

        var fragmentLengths = Utils.String2List(GetSettingsString(settings, "fragmentLengths")) ?? [];
        var fragmentDelays = Utils.String2List(GetSettingsString(settings, "fragmentDelays")) ?? [];
        var fragmentMaxSplit = GetSettingsString(settings, "fragmentMaxSplit");
        if (fragmentLengths.Any(item => !Utils.TryParseRange(item, 0, int.MaxValue, out _, out _))
            || fragmentDelays.Any(item => !Utils.TryParseRange(item, 0, int.MaxValue, out _, out _))
            || (fragmentMaxSplit.IsNotEmpty() && !Utils.TryParseMaxSplit(fragmentMaxSplit, 0, 10000, out _, out _)))
        {
            await NotifySettingsSaveAsync(false, "Fragment parameter error");
            return;
        }

        var oldEnableStatistics = config.GuiItem.EnableStatistics;
        var oldDisplayRealTimeSpeed = config.GuiItem.DisplayRealTimeSpeed;
        var oldEnableDragDropSort = config.UiItem.EnableDragDropSort;
        var oldEnableHWA = config.GuiItem.EnableHWA;

        var requestedEnableTun = GetSettingsBool(settings, "enableTun", config.TunModeItem.EnableTun);
        var tunDenied = requestedEnableTun && !AllowEnableTun();
        var tunChanged = requestedEnableTun != config.TunModeItem.EnableTun;

        // Core
        var inbound = config.Inbound.First();
        inbound.LocalPort = localPort;
        inbound.SecondLocalPortEnabled = GetSettingsBool(settings, "secondLocalPortEnabled", inbound.SecondLocalPortEnabled);
        inbound.UdpEnabled = GetSettingsBool(settings, "udpEnabled", inbound.UdpEnabled);
        inbound.SniffingEnabled = GetSettingsBool(settings, "sniffingEnabled", inbound.SniffingEnabled);
        inbound.DestOverride = GetSettingsStringArray(settings, "destOverride");
        inbound.RouteOnly = GetSettingsBool(settings, "routeOnly", inbound.RouteOnly);
        inbound.AllowLANConn = GetSettingsBool(settings, "allowLANConn", inbound.AllowLANConn);
        inbound.NewPort4LAN = GetSettingsBool(settings, "newPort4LAN", inbound.NewPort4LAN);
        inbound.User = GetSettingsString(settings, "user", inbound.User);
        inbound.Pass = GetSettingsString(settings, "pass", inbound.Pass);
        if (config.Inbound.Count > 1)
        {
            config.Inbound.RemoveAt(1);
        }
        config.CoreBasicItem.LogEnabled = GetSettingsBool(settings, "logEnabled", config.CoreBasicItem.LogEnabled);
        config.CoreBasicItem.Loglevel = GetSettingsString(settings, "loglevel", config.CoreBasicItem.Loglevel);
        config.CoreBasicItem.DefFingerprint = GetSettingsString(settings, "defFingerprint", config.CoreBasicItem.DefFingerprint);
        config.CoreBasicItem.DefUserAgent = GetSettingsString(settings, "defUserAgent", config.CoreBasicItem.DefUserAgent);
        config.CoreBasicItem.SendThrough = GetSettingsString(settings, "sendThrough", config.CoreBasicItem.SendThrough ?? string.Empty).TrimEx();
        config.CoreBasicItem.BindInterface = GetSettingsString(settings, "bindInterface", config.CoreBasicItem.BindInterface ?? string.Empty).TrimEx();
        config.Mux4SboxItem.Protocol = GetSettingsString(settings, "mux4SboxProtocol", config.Mux4SboxItem.Protocol);
        config.CoreBasicItem.EnableCacheFile4Sbox = GetSettingsBool(settings, "enableCacheFile4Sbox", config.CoreBasicItem.EnableCacheFile4Sbox);
        config.HysteriaItem.UpMbps = GetSettingsInt(settings, "hyUpMbps", config.HysteriaItem.UpMbps);
        config.HysteriaItem.DownMbps = GetSettingsInt(settings, "hyDownMbps", config.HysteriaItem.DownMbps);
        config.CoreBasicItem.EnableFragment = GetSettingsBool(settings, "enableFragment", config.CoreBasicItem.EnableFragment);
        config.CoreBasicItem.EnableFinalFragment = GetSettingsBool(settings, "enableFinalFragment", config.CoreBasicItem.EnableFinalFragment);
        config.Fragment4RayItem ??= new();
        config.Fragment4RayItem.Packets = GetSettingsString(settings, "fragmentPackets", config.Fragment4RayItem.Packets);
        config.Fragment4RayItem.Lengths = fragmentLengths;
        config.Fragment4RayItem.Delays = fragmentDelays;
        config.Fragment4RayItem.MaxSplit = fragmentMaxSplit;

        // General
        config.GuiItem.AutoRun = GetSettingsBool(settings, "autoRun", config.GuiItem.AutoRun);
        config.GuiItem.EnableStatistics = GetSettingsBool(settings, "enableStatistics", config.GuiItem.EnableStatistics);
        config.GuiItem.DisplayRealTimeSpeed = GetSettingsBool(settings, "displayRealTimeSpeed", config.GuiItem.DisplayRealTimeSpeed);
        config.GuiItem.KeepOlderDedupl = GetSettingsBool(settings, "keepOlderDedupl", config.GuiItem.KeepOlderDedupl);
        config.UiItem.EnableAutoAdjustMainLvColWidth = GetSettingsBool(settings, "enableAutoAdjustMainLvColWidth", config.UiItem.EnableAutoAdjustMainLvColWidth);
        config.UiItem.AutoHideStartup = GetSettingsBool(settings, "autoHideStartup", config.UiItem.AutoHideStartup);
        config.UiItem.Hide2TrayWhenClose = GetSettingsBool(settings, "hide2TrayWhenClose", config.UiItem.Hide2TrayWhenClose);
        config.UiItem.Minimize2Tray = GetSettingsBool(settings, "minimize2Tray", config.UiItem.Minimize2Tray);
        config.UiItem.EnableDragDropSort = GetSettingsBool(settings, "enableDragDropSort", config.UiItem.EnableDragDropSort);
        config.UiItem.DoubleClick2Activate = GetSettingsBool(settings, "doubleClick2Activate", config.UiItem.DoubleClick2Activate);
        config.GuiItem.AutoUpdateInterval = GetSettingsInt(settings, "autoUpdateInterval", config.GuiItem.AutoUpdateInterval);
        config.GuiItem.TrayMenuServersLimit = GetSettingsInt(settings, "trayMenuServersLimit", config.GuiItem.TrayMenuServersLimit);
        config.UiItem.CurrentFontFamily = GetSettingsString(settings, "currentFontFamily", config.UiItem.CurrentFontFamily);
        config.SpeedTestItem.SpeedTestTimeout = GetSettingsInt(settings, "speedTestTimeout", config.SpeedTestItem.SpeedTestTimeout);
        config.SpeedTestItem.MixedConcurrencyCount = GetSettingsInt(settings, "mixedConcurrencyCount", config.SpeedTestItem.MixedConcurrencyCount);
        config.SpeedTestItem.SpeedTestUrl = GetSettingsString(settings, "speedTestUrl", config.SpeedTestItem.SpeedTestUrl);
        config.SpeedTestItem.SpeedPingTestUrl = GetSettingsString(settings, "speedPingTestUrl", config.SpeedTestItem.SpeedPingTestUrl);
        config.SpeedTestItem.UdpTestTarget = GetSettingsString(settings, "udpTestTarget", config.SpeedTestItem.UdpTestTarget);
        config.SpeedTestItem.IPAPIUrl = GetSettingsString(settings, "ipapiUrl", config.SpeedTestItem.IPAPIUrl);
        var requestedHwa = GetSettingsBool(settings, "enableHWA", config.GuiItem.EnableHWA);
        if (requestedHwa && !oldEnableHWA)
        {
            // Explicitly re-enabled after an auto-disable: give the GPU a
            // fresh chance by resetting the crash budget instead of inheriting
            // the auto-disabled state forever.
            HardwareAccelerationGuard.OnUserReEnabled();
        }
        config.GuiItem.EnableHWA = requestedHwa;
        config.GuiItem.EffectsMode = NormalizeEffectsMode(GetSettingsString(settings, "effectsMode", config.GuiItem.EffectsMode));
        config.GuiItem.ReduceEffects = config.GuiItem.EffectsMode == "reduced";
        config.ConstItem.SubConvertUrl = GetSettingsString(settings, "subConvertUrl", config.ConstItem.SubConvertUrl);
        config.ConstItem.GeoSourceUrl = GetSettingsString(settings, "geoFileSourceUrl", config.ConstItem.GeoSourceUrl);
        config.ConstItem.SrsSourceUrl = GetSettingsString(settings, "srsFileSourceUrl", config.ConstItem.SrsSourceUrl);
        config.ConstItem.RouteRulesTemplateSourceUrl = GetSettingsString(settings, "routingRulesSourceUrl", config.ConstItem.RouteRulesTemplateSourceUrl);
        config.GuiItem.RootCertProvider = GetSettingsString(settings, "rootCertProvider", config.GuiItem.RootCertProvider);

        // System proxy
        var requestedSysProxyType = (ESysProxyType)Math.Clamp(
            GetSettingsInt(settings, "sysProxyType", (int)config.SystemProxyItem.SysProxyType), 0, 3);
        config.SystemProxyItem.SystemProxyExceptions = GetSettingsString(settings, "systemProxyExceptions", config.SystemProxyItem.SystemProxyExceptions);
        config.SystemProxyItem.NotProxyLocalAddress = GetSettingsBool(settings, "notProxyLocalAddress", config.SystemProxyItem.NotProxyLocalAddress);
        config.SystemProxyItem.SystemProxyAdvancedProtocol = GetSettingsString(settings, "systemProxyAdvancedProtocol", config.SystemProxyItem.SystemProxyAdvancedProtocol);
        config.SystemProxyItem.CustomSystemProxyPacPath = GetSettingsString(settings, "customSystemProxyPacPath", config.SystemProxyItem.CustomSystemProxyPacPath);
        config.SystemProxyItem.CustomSystemProxyScriptPath = GetSettingsString(settings, "customSystemProxyScriptPath", config.SystemProxyItem.CustomSystemProxyScriptPath);
        config.SystemProxyItem.SysProxyType = requestedSysProxyType;

        // TUN mode
        config.TunModeItem.AutoRoute = GetSettingsBool(settings, "tunAutoRoute", config.TunModeItem.AutoRoute);
        config.TunModeItem.StrictRoute = GetSettingsBool(settings, "tunStrictRoute", config.TunModeItem.StrictRoute);
        config.TunModeItem.Stack = GetSettingsString(settings, "tunStack", config.TunModeItem.Stack);
        config.TunModeItem.Mtu = GetSettingsInt(settings, "tunMtu", config.TunModeItem.Mtu);
        config.TunModeItem.EnableIPv6Address = GetSettingsBool(settings, "tunEnableIPv6Address", config.TunModeItem.EnableIPv6Address);
        config.TunModeItem.IcmpRouting = GetSettingsString(settings, "tunIcmpRouting", config.TunModeItem.IcmpRouting);
        config.TunModeItem.EnableLegacyProtect = GetSettingsBool(settings, "tunEnableLegacyProtect", config.TunModeItem.EnableLegacyProtect);
        config.TunModeItem.RouteExcludeAddress = Utils.String2List(GetSettingsString(settings, "tunRouteExcludeAddress"));
        config.TunModeItem.IPv4Address = GetSettingsString(settings, "tunIPv4Address", config.TunModeItem.IPv4Address);
        config.TunModeItem.IPv6Address = GetSettingsString(settings, "tunIPv6Address", config.TunModeItem.IPv6Address);
        config.TunModeItem.EnableTun = tunDenied ? false : requestedEnableTun;

        // Re-assert the fail-safe route defaults after the settings form overwrites
        // the TUN block. The stability policy is applied on every config load; this
        // keeps AutoRoute / StrictRoute / EnableLegacyProtect pinned on even when
        // the user saves a custom MTU or stack, so TUN stays leak-free.
        if (Utils.IsWindows())
        {
            WindowsTunStabilityPolicy.Apply(config);
        }
        else
        {
            config.TunModeItem.Mtu = WindowsTunStabilityPolicy.NormalizeMtu(config.TunModeItem.Mtu);
            config.TunModeItem.Stack = WindowsTunStabilityPolicy.NormalizeStack(config.TunModeItem.Stack);
        }

        // Core types
        SaveSettingsCoreTypes(settings);

        var needReboot = oldEnableStatistics != config.GuiItem.EnableStatistics
            || oldDisplayRealTimeSpeed != config.GuiItem.DisplayRealTimeSpeed
            || oldEnableDragDropSort != config.UiItem.EnableDragDropSort
            || oldEnableHWA != config.GuiItem.EnableHWA;

        var saved = await ConfigHandler.SaveConfig(config) == 0;
        if (saved)
        {
            await AutoStartupHandler.UpdateTask(config);
            AppManager.Instance.Reset();

            // Apply the complete AoGPN system-proxy setting immediately, including
            // exceptions and PAC/script changes even when the mode number is unchanged.
            await SysProxyHandler.UpdateSysProxy(config, false, requestedSysProxyType);
            // If the saved preference became Set/PAC while the connection is off,
            // start the proxy-only core so the OS proxy points at a live listener
            // (the same reconcile the dashboard and status-bar paths perform).
            await _proxyOnlyService.ReconcileAsync(config);
            StatusBarViewModel.Instance.SystemProxySelected = (int)requestedSysProxyType;
            await PushSystemProxyStateAsync(force: true);

            // TUN toggle changed: persist and reload the core (native status bar behaviour).
            if (tunChanged && !tunDenied)
            {
                StatusBarViewModel.Instance.ReloadRequested.Publish();
            }

            // Resync the whole form with the persisted truth so the shown switches
            // always match the config the close/minimize paths read (e.g. after the
            // TUN admin denial reverted enableTun above).
            await PushSettingsAsync();
        }

        // The effects tier applies immediately — push it even when the save
        // itself failed so the renderer never drifts from what the user picked.
        await PushEffectsTierAsync();

        var message = tunDenied
            ? "TUN mode requires administrator privileges — it was not enabled"
            : needReboot ? "Settings saved — a restart is required for some changes" : "Settings saved";
        await NotifySettingsSaveAsync(saved, message, needReboot, tunDenied);
    }

    private void SaveSettingsCoreTypes(JsonElement settings)
    {
        foreach (var item in AppManager.Instance.Config.CoreTypeItem ?? [])
        {
            var value = GetSettingsString(settings, $"coreType{(int)item.ConfigType}", item.CoreType.ToString());
            if (Enum.TryParse<ECoreType>(value, true, out var parsed))
            {
                item.CoreType = parsed;
            }
        }
    }

    /// <summary>Sends the Settings save acknowledgement back to the renderer.</summary>
    private async Task NotifySettingsSaveAsync(
        bool ok,
        string message,
        bool needReboot = false,
        bool tunDenied = false)
    {
        if (!_webViewReady)
        {
            return;
        }
        var json = JsonSerializer.Serialize(new { ok, message, needReboot, tunDenied });
        await ExecuteScriptSafelyAsync($"window.setSettingsSaveResult({json});");
    }

    private static bool GetSettingsBool(JsonElement settings, string name, bool fallback)
    {
        if (!settings.TryGetProperty(name, out var property))
        {
            return fallback;
        }
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(property.GetString(), out var parsed) ? parsed : fallback,
            JsonValueKind.Number => property.GetInt32() != 0,
            _ => fallback,
        };
    }

    private static int GetSettingsInt(JsonElement settings, string name, int fallback)
    {
        if (!settings.TryGetProperty(name, out var property))
        {
            return fallback;
        }
        if (property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out var num))
        {
            return num;
        }
        if (property.ValueKind == JsonValueKind.String
            && int.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }
        return fallback;
    }

    private static string GetSettingsString(JsonElement settings, string name, string? fallback = "")
    {
        if (settings.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString() ?? fallback ?? string.Empty;
        }
        return fallback ?? string.Empty;
    }

    private static List<string> GetSettingsStringArray(JsonElement settings, string name)
    {
        var result = new List<string>();
        if (!settings.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return result;
        }
        foreach (var item in property.EnumerateArray())
        {
            var value = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (value.IsNotEmpty())
            {
                result.Add(value);
            }
        }
        return result;
    }

    /// <summary>Mirrors StatusBarViewModel.AllowEnableTun: TUN needs elevation everywhere.</summary>
    private static bool AllowEnableTun()
    {
        if (Utils.IsWindows())
        {
            return Utils.IsAdministrator();
        }
        else if (Utils.IsLinux() || Utils.IsMacOS())
        {
            return AppManager.Instance.LinuxSudoPwd.IsNotEmpty();
        }
        return false;
    }

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
        _gpnPidBridge?.Dispose();
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

        _dashboardHost.WebMessageReceived -= CoreWebView2_WebMessageReceived;
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
}
