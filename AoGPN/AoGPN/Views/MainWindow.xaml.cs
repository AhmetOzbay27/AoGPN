using System.Reactive.Disposables;
using System.Reactive.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private ThemeSettingViewModel? _sidebarThemeVm;

    // Route-test results are pushed to the renderer with camelCase keys to match the
    // rest of the host→renderer payloads.
    private static readonly JsonSerializerOptions RouteTestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly CancellationTokenSource _webViewLifetime = new();
    private readonly SemaphoreSlim _connectionToggleGate = new(1, 1);
    // Bağlan/kes/mod değişimi komutlarının TEK kapısı (ConnectionCommandGate):
    // kapı meşgulken tıklamalar asla sessizce düşmez — en yeni niyet saklanır,
    // kapı boşalınca UI thread'de tam olarak bir kez işlenir. (System-proxy
    // komutları eski _connectionToggleGate'i kullanmaya devam eder.)
    private readonly ConnectionCommandGate _commandGate = null!;
    private readonly ConnectionLifecycleSupervisor _lifecycleSupervisor;
    private TelemetryDashboardViewModel? _telemetryDashboard;
    private bool _webViewReady;

    /// <summary>
    /// WebView2 denetleyicisi kapandıktan sonra dokunmayı kesen kalıcı kilit (bkz.
    /// <see cref="WebViewTouchLatch"/>). Denetleyici nesnesi null olmadığı için
    /// tek başına null kontrolü yeterli değildir.
    /// </summary>
    private readonly WebViewTouchLatch _webViewLatch = new();
    private bool _connectionState;
    private bool _connectionStarting;
    // Otomatik çekirdek kurtarma (beklenmedik çıkış → yeniden başlatma) penceresinde
    // dashboard "kopmuş → Bağlan" yerine "yeniden bağlanıyor" göstersin — çökme
    // kurtarmasının başladığını CoreHealthChanged üzerindeki Degraded+Recovering
    // olayı işaretler (bkz. OnMainCoreHealthChangedAsync). 0/1 Volatile bayrak.
    private int _autoRecovering;

    // GPN WG oturumunun gerçekten çıktığı sunucu (koordinatör snapshot'ından).
    // Çıkış-ülkesi doğrulamasında mihomo düğümü yerine BU kullanılır: WG tüneli
    // birincil egress'tir, mihomo düğüm seçimi ayrı bir katmandır (bkz. CheckIpAsync).
    private ServiceLib.Services.GpnServerProfile? _activeGpnServer;

    // Bağlantı-SONRASI gerçek ping ölçümü oturum başına yalnızca BİR kez koşar.
    // Yumuşak düğüm geçişi ve failover da "Connected" yayınlar; ağır ölçümü her
    // düğüm değişiminde tekrarlamak taze tünelin ilk saniyelerini boşa meşgul eder
    // (bkz. docs/gaming-connect-performance-plan.md A4). Kopmada sıfırlanır.
    private bool _realPingAfterMeasured;


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

    // App.OnStartup parks the window OFF-SCREEN while WebView2 boots the dashboard
    // (never "Opacity 0" — that needs a layered window, which paints solid black on
    // machines with broken compositing; the boot splash covers the gap). True until
    // the dashboard has painted its first frame (see RevealStartupWindow).
    private bool _dashboardFirstPaintPending = true;

    // While the dashboard is booting, WindowBase must not restore the saved window
    // placement: the window is parked at -32000,-32000, and applying the saved
    // size/position/maximize there would drag it on screen mid-boot.
    // RevealStartupWindow applies the placement (ApplyStartupPlacement) in the same
    // tick the dashboard becomes visible.
    protected override bool DeferPlacementUntilReveal => _dashboardFirstPaintPending;

    // Set once the initial dashboard state has been pushed (or the push failed);
    // guards the two ready signals (NavigationCompleted / readyState poll) so the
    // seeds run exactly once.
    private bool _startupSeeded;

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
    // Bağlantı kurulamayınca dashboard hata kartına taşınan başarısızlık defteri
    // (ConnectionFailureLedger — kayıtlar, öncelik ve 45 sn tazelik orada yaşar).
    private readonly ConnectionFailureLedger _failureLedger;
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
        // Bağlantı komut kapısı: kuyruklanan komutlar UI thread'de koşar — WebView2
        // ve ViewModel erişimi UI thread gerektirir (Task.Run ile yeniden dağıtım
        // COMException/wrong-thread üretip kuyruklu tıklamayı sessizce düşürüyordu).
        _commandGate = new ConnectionCommandGate(
            action => Dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task.Unwrap());
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
            getMainViewModel: () => ViewModel,
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
        _lifecycleSupervisor = new ConnectionLifecycleSupervisor(
            runOnUiThread: action => Dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task.Unwrap(),
            readConnected: () => Volatile.Read(ref _connectionState),
            readLastTunnelVerified: () => _lastTunnelVerified,
            synchronizeConnectionState: () => SynchronizeConnectionStateAsync(),
            pushRuleDrift: () => PushRuleDriftAsync(),
            checkIp: CheckIpAsync,
            suggestRealityCoreFallback: SuggestRealityCoreFallbackAsync,
            updateTrayStatus: () =>
            {
                UpdateTrayStatus();
                return Task.CompletedTask;
            },
            pushSystemProxyState: () => PushSystemProxyStateAsync(),
            pushMonitorSnapshot: () => PushMonitorSnapshotAsync(),
            pushNodeInfo: () => PushNodeInfoAsync(),
            synchronizeWindowState: SynchronizeWindowStateAsync);
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
        _failureLedger = new ConnectionFailureLedger(
            executeScript: ExecuteScriptSafelyAsync,
            isWebViewReady: () => _webViewReady,
            readActualConnectionState: ReadActualConnectionState);

        AppEvents.CoreHealthChanged.AsObservable()
            .Subscribe(health =>
            {
                _failureLedger.RecordCoreHealth(health);
                // Otomatik kurtarma penceresini UI durumuna çevir (Dispatcher: health
                // olayları çekirdek yönetiminden gelir; ViewModel/WebView2 yalnızca UI
                // iş parçacığında okunur).
                if (health.Role == CoreHealthRole.Main)
                {
                    _ = Dispatcher.InvokeAsync(() => OnMainCoreHealthChangedAsync(health));
                }
            });
        AppEvents.CoreStartupDiagnosticChanged.AsObservable()
            .Subscribe(_failureLedger.RecordDiagnostic);

        ThreadPool.RegisterWaitForSingleObject(App.ProgramStarted, OnProgramStarted, null, -1, false);

        App.Current.SessionEnding += Current_SessionEnding;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        btnNavMsg.Click += (_, _) => { SetActiveNav(btnNavMsg); tabMain2.SelectedIndex = 0; };
        btnNavImport.Click += (_, _) => SetActiveNav(btnNavImport);
        btnNavScan.Click += (_, _) => SetActiveNav(btnNavScan);
        btnNavConnection.Click += (_, _) => { SetActiveNav(btnNavConnection); tabMain2.SelectedIndex = 3; };
        btnNavGameBoost.Click += (_, _) => { SetActiveNav(btnNavGameBoost); tabMain2.SelectedIndex = 3; };
        btnNavProxies.Click += (_, _) => { SetActiveNav(btnNavProxies); tabMain2.SelectedIndex = 1; };
        btnNavConnections.Click += (_, _) => { SetActiveNav(btnNavConnections); tabMain2.SelectedIndex = 2; };
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
                // Keep the persisted dashboard theme in lockstep so the WebView2
                // dashboard survives restarts with the same palette when the user
                // switches themes from the native sidebar.
                _config.UiItem.DashboardTheme = MapWpfThemeToWeb(theme);
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
            //servers (rail: clipboard/QR only)
            this.BindCommand(ViewModel, vm => vm.AddServerViaClipboardCmd, v => v.btnNavImport).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddServerViaScanCmd, v => v.btnNavScan).DisposeWith(disposables);

            //setting (rail)
            this.BindCommand(ViewModel, vm => vm.OptionSettingCmd, v => v.btnNavSettings).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RoutingSettingCmd, v => v.btnNavRouting).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.DNSSettingCmd, v => v.btnNavDNS).DisposeWith(disposables);

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

            // Oturum hayatta kalma probu: çekirdek kurtarma/soft-reload
            // pencerelerinde TCP+UDP bacaklarının kesilip kesilmediğini ölçer ve
            // sonucu diag'e "SURVIVAL tcp=… udp=… gapMs=…" olarak yazar
            // (bkz. SessionSurvivalProbeService — docs/session-continuity-design.md).
            SessionSurvivalProbeService.Instance.Start();
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

        // Windows 11 draws an accent-colored frame band around borderless
        // thick-frame windows (follows the user's "Show accent color on title
        // bars and window borders" setting — the purple line, unchanged by the
        // app theme). Disabling DWM rendering removes it but falls back to the
        // classic cream-colored frame, so instead the non-client area is
        // collapsed to zero via WM_NCCALCSIZE in WindowProc: neither the
        // accent band nor the classic frame is ever visible and the WebView2
        // content fills the window edge to edge. Resize hit-testing is
        // restored manually (WM_NCHITTEST); maximized bounds are clamped to
        // the work area in WM_GETMINMAXINFO.
        HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
    }

    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcCalcSize = 0x0083;
    private const int WmNcHitTest = 0x0084;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
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

        if (msg == WmNcCalcSize)
        {
            // Client area = whole window: the DWM accent frame band and the
            // classic fallback frame are never visible, so the WebView2 content
            // fills the window edge to edge in windowed mode too.
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WmNcHitTest && WindowState != WindowState.Maximized)
        {
            // With the non-client area collapsed DefWindowProc reports HTCLIENT
            // everywhere and edge-resize would be dead; restore the resize
            // handles explicitly (lParam = screen coordinates).
            var x = (short)((long)lParam & 0xFFFF);
            var y = (short)(((long)lParam >> 16) & 0xFFFF);
            var hit = HitTestResizeEdges(hwnd, x, y);
            if (hit != 0)
            {
                handled = true;
                return new IntPtr(hit);
            }
            return IntPtr.Zero;
        }

        if (msg != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        // WPF's borderless maximize sizes the window to the whole monitor; clamp
        // it to the work area of the monitor the window is on (multi-monitor safe)
        // so the taskbar stays visible and no content is cut off underneath it.
        // With the non-client area collapsed (WM_NCCALCSIZE) the window rect IS
        // the client rect, so the maximized rect is the work area exactly.
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (GetMonitorInfo(monitor, ref info))
        {
            var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            mmi.ptMaxPosition.X = info.rcWork.Left;
            mmi.ptMaxPosition.Y = info.rcWork.Top;
            mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
            mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
            Marshal.StructureToPtr(mmi, lParam, false);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private int HitTestResizeEdges(IntPtr hwnd, int screenX, int screenY)
    {
        GetWindowRect(hwnd, out var r);
        var grip = Math.Max(6, (int)(6 * GetDpiForWindow(hwnd) / 96.0));
        var left = screenX - r.Left;
        var right = r.Right - screenX;
        var top = screenY - r.Top;
        var bottom = r.Bottom - screenY;
        var onLeft = left >= 0 && left <= grip;
        var onRight = right >= 0 && right <= grip;
        var onTop = top >= 0 && top <= grip;
        var onBottom = bottom >= 0 && bottom <= grip;
        if (onLeft && onTop) return HtTopLeft;
        if (onRight && onTop) return HtTopRight;
        if (onLeft && onBottom) return HtBottomLeft;
        if (onRight && onBottom) return HtBottomRight;
        if (onLeft) return HtLeft;
        if (onRight) return HtRight;
        if (onTop) return HtTop;
        if (onBottom) return HtBottom;
        return 0;
    }

    /// <summary>
    /// Creates the WebView2 environment, applies safe browser settings, and serves the
    /// packaged dashboard through a fixed virtual host instead of an arbitrary file URL.
    /// </summary>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;

        // The dashboard always opens maximized so it fills the work area instead
        // of the default small frame — ApplyStartupPlacement does this at the
        // reveal; this block is only the reload-after-boot fallback. While the
        // dashboard is booting the window is parked off-screen (see App.OnStartup)
        // and placement is applied by ApplyStartupPlacement at the reveal — this
        // block only runs if the window is ever reloaded after a completed boot.
        if (!_dashboardFirstPaintPending)
        {
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
        }

        // Safety net: if the dashboard never raises NavigationCompleted — or the
        // WebView2 initialization below hangs — never leave the off-screen startup
        // window (parked at -32000,-32000 + Hidden WebView2) hidden forever.
        // Started BEFORE InitializeWebViewAsync so it also covers an init hang. The
        // reveal itself is idempotent, so this can only help.
        var revealSafety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        revealSafety.Tick += (_, _) =>
        {
            revealSafety.Stop();
            RevealStartupWindow();
        };
        revealSafety.Start();

        // The splash status line is fixed (see SplashWindow); only the bar
        // advances here as the dashboard boots.
        App.Splash?.SetProgress(60);
        DiagLog.Write("WEBVIEW_BOOT webview-init-start");

        try
        {
            await InitializeWebViewAsync();
            DiagLog.Write("WEBVIEW_BOOT webview-init-done");
            // Fast ready signal: NavigationCompleted can lag 2+ seconds behind the
            // page's own load on loaded/virtualized machines, so also poll the
            // document.readyState directly. Whichever fires first runs the seeds.
            _ = PollForDashboardReadyAsync();
        }
        catch (Exception ex)
        {
            // WebView2 never came up: reveal the window so the error dialog below is
            // visible instead of a silent invisible window.
            RevealStartupWindow();
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
            // Navigation failed: never leave the off-screen startup window hidden.
            RevealStartupWindow();
            Logging.SaveLog($"AoGPN dashboard navigation failed: {e.WebErrorStatus}");
            return;
        }

        // Normal ready signal. On machines where NavigationCompleted lags far behind
        // the page's own load event (measured 2+ s), the readiness poll started in
        // MainWindow_Loaded gets here first; _startupSeeded keeps both paths safe.
        if (!_startupSeeded)
        {
            DiagLog.Write("WEBVIEW_BOOT nav-fired");
            await RunStartupSeedsAsync();
        }
    }

    /// <summary>
    /// Pushes the initial dashboard state (theme/settings/language/nodes/…), reveals
    /// the startup window, then completes the remaining boot work. Runs exactly once
    /// — triggered by whichever comes first: NavigationCompleted or the
    /// document.readyState poll (see <see cref="PollForDashboardReadyAsync"/>).
    /// </summary>
    private async Task RunStartupSeedsAsync()
    {
        if (_isClosing || _startupSeeded)
        {
            return;
        }

        _startupSeeded = true;
        try
        {
            _webViewReady = true;
            App.Splash?.SetProgress(80);
            DiagLog.Write("WEBVIEW_BOOT seeds-start");
            // If AutoHideStartup left the window minimized, freeze the WebView2
            // right away so the hidden dashboard never starts compositing.
            await SyncWebViewSuspensionAsync(WindowState == WindowState.Minimized);

            // Critical first-paint state, pushed in parallel: the pushes are
            // independent JS setters and each await releases the UI thread between
            // browser round-trips, so concurrent pushes overlap instead of
            // serializing.
            await Task.WhenAll(
                SynchronizeConnectionStateAsync(forcePublish: true),
                SynchronizeWindowStateAsync(),
                PushNodeListAsync(),
                PushSettingsAsync(),
                PushMonitorSnapshotAsync(force: true),
                PushThemeAsync(ResolveInitialDashboardTheme()),
                PushEffectsTierAsync(),
                PushLanguageAsync());

            // The dashboard has now loaded and been seeded with the initial state
            // (theme, settings, language, ...): reveal the startup window. It was
            // shown at Opacity 0 so launching never flashes an empty black frame
            // while WebView2 boots (the boot splash covers that phase and
            // cross-fades out here). Note: a renderer-side "first paint" signal
            // cannot be used while the WebView is still hidden — WebView2
            // suspends the hidden renderer, so page timers/fetches never run
            // until the reveal unfreezes it. The AutoRun wait and the proxy-only
            // reconcile below can take seconds and must not delay the window
            // from appearing.
            DiagLog.Write("WEBVIEW_SEEDED initial-state-pushed");
            RevealStartupWindow();

            // Secondary state (banners/telemetry/app info) — pushed after the
            // reveal so the splash hands off on the critical state alone; their
            // event feeds republish them anyway.
            try
            {
                await Task.WhenAll(
                    PushGpnTelemetryAsync(),
                    PushGpnCaptureStatsAsync(),
                    PushWinDivertHealthAsync(),
                    PushWarpHealthAsync(),
                    PushAppInfoAsync(),
                    ShowHwaFallbackNoticeIfNeededAsync());
            }
            catch (Exception ex)
            {
                Logging.SaveLog("AoGPN dashboard secondary state push failed", ex);
            }

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

            // Yeni sürüm denetimi: arayüz göründükten SONRA, arka planda. Yeni sürüm
            // varsa paket indirilir ve indirme bitince kullanıcıya onay sorulur.
            // Bağlantı kurulumunu, yönlendirmeyi ve yakalama katmanını etkilemez.
            _ = CheckForAppUpdateAsync();

            // Faz 3 — açılış ön yüklemesi: GPN sunucu ölçümleri ısıtılır ve çekirdek
            // ikilileri kontrol edilir. Pahalı ama bağlanmadan bağımsız işler burada
            // yapılır; bağlanma anı yalnızca bağlantı işiyle meşgul kalır.
            _ = new StartupPreflight(
                shouldWarmGpnProbes: () => ViewModel?.IsGpnFlowConfigured() == true,
                warmGpnProbeCache: ct => ViewModel?.WarmGpnProbeCacheAsync(ct) ?? Task.CompletedTask,
                log: DiagLog.Write,
                notify: message => NoticeManager.Instance.Enqueue(message))
                .RunAsync(_webViewLifetime.Token);

        }
        catch (Exception ex)
        {
            // Contain unexpected browser teardown errors so they cannot escape onto
            // WPF's dispatcher as fatal exceptions. A failed startup script must
            // never leave the off-screen window hidden forever, so reveal anyway
            // (the dashboard may still be usable).
            RevealStartupWindow();
            if (!_isClosing)
            {
                Logging.SaveLog("AoGPN dashboard startup script failed", ex);
            }
        }
    }

    /// <summary>
    /// Açılışta (arayüz göründükten sonra) GitHub Releases API'sinden yeni sürüm
    /// denetler; yeni sürüm varsa paketi arka planda indirir ve indirme BİTİNCE
    /// kullanıcıya onay sorar. Onay verilirse <c>AoGPN.Updater</c> başlatılır ve
    /// uygulama DÜZENLİ kapanış yolundan (çekirdeği durdur → proxy temizle →
    /// config flush) sonlandırılır; güncelleyici bu kapanışı bekleyip dosyaları
    /// değiştirir ve uygulamayı yeniden başlatır.
    ///
    /// Hiçbir aşama bağlantıyı, yönlendirmeyi veya yakalama katmanını etkilemez:
    /// yalnızca bir HTTPS GET, bir indirme ve bir süreç başlatmadır. Tüm hatalar
    /// yutulur — güncelleme denetimi asla açılışı veya bağlantıyı bozmaz.
    /// </summary>
    private async Task CheckForAppUpdateAsync()
    {
        if (_isClosing)
        {
            return;
        }

        try
        {
            var update = await AppUpdateChecker.Instance.CheckAsync().ConfigureAwait(true);
            if (update is null || !update.IsNewer)
            {
                return;
            }

            Logging.SaveLog($"[AppUpdate] Yeni sürüm bulundu: {update.Tag} (yüklü: {Utils.GetVersionInfo()})");

            // Platforma uyan yayın varlığı (asset) yoksa indirilecek bir şey de
            // yoktur: kullanıcıya tamamlanamayacak bir güncelleme sunmak yerine
            // durumu yalnızca günlüğe yaz. Sürüm yayınlanırken beklenen dosya adı
            // kullanılmalıdır (ör. AoGPN-windows-64.zip).
            if (update.AssetUrl.IsNullOrEmpty())
            {
                Logging.SaveLog($"[AppUpdate] {update.Tag} sürümünde platforma uygun paket bulunamadı "
                    + $"(beklenen: {AppUpdateChecker.ExpectedAssetName() ?? "desteklenmeyen platform"}) — "
                    + "güncelleme sunulmuyor.");
                return;
            }

            // Güncelleyici yan uygulaması kurulum klasöründe yoksa (ör. eski bir
            // elle kurulum) güncelleme sunulmaz: dosyaları yerine koyacak bileşen
            // olmadan indirme yapmak yalnızca boşa bant genişliği olurdu.
            if (AppUpdateInstaller.ResolveUpdaterPath() is null)
            {
                Logging.SaveLog($"[AppUpdate] {Utils.GetExeName(AppUpdateInstaller.UpdaterExeName)} "
                    + "bulunamadı — güncelleme kullanıcıya sunulmuyor.");
                return;
            }

            var installer = new AppUpdateInstaller();
            var window = new UpdateAvailableWindow(update, installer) { Owner = this };
            window.StartDownload();
            window.ShowDialog();

            if (!window.InstallRequested || window.DownloadedZipPath is not { Length: > 0 } zipPath)
            {
                return;
            }

            if (!installer.TryLaunchUpdater(zipPath, Environment.ProcessId, out var error))
            {
                NoticeManager.Instance.Enqueue($"Güncelleme başlatılamadı: {error}");
                return;
            }

            // Güncelleyici artık bu sürecin PID'sini bekliyor. Dosyaların üzerine
            // yazılabilmesi için çekirdeği durduran ve proxy'yi temizleyen düzenli
            // kapanış yolundan çık.
            _allowClose = true;
            await ExitApplicationSafelyAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("[AppUpdate] Güncelleme denetimi hatası", ex);
        }
    }

    /// <summary>
    /// Waits for the dashboard document to finish loading by polling
    /// document.readyState over ExecuteScriptAsync instead of relying solely on
    /// NavigationCompleted, which on some machines (loaded CPUs, virtualized
    /// displays) lags 2+ seconds behind the page's own load event. The page-side
    /// load is what actually matters for the seeds, and ExecuteScriptAsync is a
    /// fast, proven channel even while the WebView is hidden. Capped and
    /// failure-safe: NavigationCompleted remains the authority for failed
    /// navigations, and the 15 s reveal safety net still stands.
    /// </summary>
    private async Task PollForDashboardReadyAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!_isClosing && !_startupSeeded && DateTime.UtcNow < deadline)
        {
            try
            {
                if (WebView.CoreWebView2 is null)
                {
                    await Task.Delay(120);
                    continue;
                }

                var result = await WebView.CoreWebView2.ExecuteScriptAsync("document.readyState");
                if (string.Equals(result, "\"complete\"", StringComparison.Ordinal))
                {
                    DiagLog.Write("WEBVIEW_BOOT ready-poll-fired");
                    await RunStartupSeedsAsync();
                    return;
                }
            }
            catch
            {
                // Document not created yet (navigation still starting) — retry.
            }

            await Task.Delay(120);
        }

        // 15 s cap: reveal anyway (idempotent) so nothing can stay invisible.
        if (!_startupSeeded)
        {
            RevealStartupWindow();
        }
    }

    /// <summary>
    /// Applies the same mode transition used by the native ConnectionView. The command
    /// is serialized so repeated clicks cannot overlap rule writes or core reloads.
    /// Kapı meşgulse tıklama asla sessizce düşmez: <see cref="ConnectionCommandGate"/>
    /// en yeni niyeti saklar ve kapı boşalınca tam bir kez, UI thread'de işler.
    /// <paramref name="displayedConnected"/> isteği gönderen arayüzün ekranda
    /// GÖSTERDİĞİ bağlantı durumudur (dashboard <c>toggle_connection</c> yükü):
    /// verildiğinde bağlan/kes yönü canlı durumdan değil bu niyetten türetilir
    /// (bkz. <see cref="ConnectionTogglePolicy"/>).
    /// </summary>
    public Task ToggleConnectionAsync(
        string requestedMode,
        string transport,
        bool? displayedConnected = null)
        => _commandGate.RunAsync(
            "toggle",
            () => ToggleConnectionCoreAsync(requestedMode, transport, displayedConnected));

    private async Task ToggleConnectionCoreAsync(
        string requestedMode,
        string transport,
        bool? displayedConnected)
    {
        try
        {
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

            DiagLog.Write($"STATE ui toggle requested mode={requestedMode} transport={transport} actualConnected={ReadActualConnectionState()} autoRecovering={Volatile.Read(ref _autoRecovering) == 1} displayedConnected={displayedConnected?.ToString() ?? "-"}");
            // Otomatik kurtarma (çekirdek çökmesi → yeniden başlatma) penceresinde
            // gelen toggle, "Bağlan" isteği DEĞİL kullanıcının kurtarmayı iptal
            // edip temiz kesme isteğidir — recovery Ready dönmeden gerçek durumu
            // yayınla, çekirdeği durdur.
            if (Volatile.Read(ref _autoRecovering) == 1)
            {
                _connectionStarting = false;
                Volatile.Write(ref _autoRecovering, 0);
                Volatile.Write(ref _connectionState, false);
                await SendConnectionStateAsync();
                var cancelViewModel = ViewModel?.ConnectionViewModel;
                if (cancelViewModel is not null)
                {
                    cancelViewModel.Transport = "";
                    await ApplyConnectionModeAsync(cancelViewModel, SplitTunnelViewModel.ModeOff);
                }
                DiagLog.Write("STATE ui disconnect (auto-recovery cancelled by user)");
                return;
            }

            var connectionViewModel = ViewModel?.ConnectionViewModel;
            if (connectionViewModel is null)
            {
                return;
            }

            // Kullanıcının EKRANDA GÖRDÜĞÜ durum (dashboard yükü) bildirilmişse
            // niyet ondan türetilir ve canlı durum bu niyeti EZEMEZ: açılışta tünel
            // arka planda kurulurken arayüz hâlâ "Bağlan" gösterirken gelen tıklama
            // eskiden "kes"e dönüşüp taze tüneli yıkıyordu (canlı gözlenen
            // "bağlandı → Bağlan'a döndü → tekrar basınca bağlandı").
            var intent = ConnectionTogglePolicy.IntentFromDisplayedState(displayedConnected);
            var effectiveConnected = ReadEffectiveConnectionState();
            var connectionStarting = Volatile.Read(ref _connectionStarting);
            if (intent is { } knownIntent)
            {
                var toggleAction = ConnectionTogglePolicy.Decide(knownIntent, effectiveConnected, connectionStarting);
                if (toggleAction == ConnectionToggleAction.NoOp)
                {
                    // Niyet zaten karşılanmış: yeni bir geçiş başlatma ve kullanıcının
                    // gördüğü durumu bozacak bir yayın yapma (süren geçiş akışı kendi
                    // durumunu yayınlar).
                    DiagLog.Write($"STATE ui toggle no-op (intent={knownIntent} displayed={displayedConnected} effective={effectiveConnected} starting={connectionStarting})");
                    return;
                }
                effectiveConnected = toggleAction == ConnectionToggleAction.Disconnect;
            }

            // Bağlantı hâlâ kurulurken gelen yeni toggle (niyet BİLDİRİLMEMİŞ — eski
            // gönderen, tepsi, iç yeniden bağlanma): kullanıcı vazgeçti. Üst üste
            // ikinci bir bağlanma SIRALAMAK yerine süregiden denemeyi iptal edip
            // temiz kesme uygula — "tıkladım ama olmuyor" hissinin ve üst üste
            // ConnectAsync yarışının ana kaynağıydı. Niyet bildirilmişse bu dal
            // devre dışıdır: "bağlan" isteği uçuştaki bağlanmayı iptal etmez,
            // "kes" isteği aşağıdaki deterministik kesme dalından yürür.
            if (intent is null && connectionStarting)
            {
                _connectionStarting = false;
                Volatile.Write(ref _connectionState, false);
                await SendConnectionStateAsync();
                connectionViewModel.Transport = "";
                await PersistConnectionModeAsync(connectionViewModel, SplitTunnelViewModel.ModeOff);
                await ApplyConnectionModeAsync(connectionViewModel, SplitTunnelViewModel.ModeOff);
                VpnSessionLog.EndSession("VPN disconnect");
                DiagLog.Write("STATE ui cancel in-flight connect + disconnect (user toggle)");
                return;
            }

            // Niyet bildirilmemişse (yukarıdaki dal atlandı) karar canlı duruma
            // dayanır ve yalnızca çekirdek sağlığına bakmaz: GPN koordinatörü canlı
            // (soft geçiş) veya son yayınlanan durum "bağlı" ise gerçek bir kesme
            // isteğidir — aksi halde bayat görünüm kesmeyi "bağlan"a çevirirdi.
            if (effectiveConnected)
            {
                // Deterministik kesme: önce kullanıcının niyetini YAYINLA (buton
                // tıklamayla birlikte "Bağlan"a döner), sonra teardown'ı yürüt.
                // _connectionStarting geçiş bayrağı teardown BOYUNCA set kalır —
                // 2 sn'lik supervisor, çekirdek hâlâ kapanırken "bağlı" yeniden
                // yayınlayamaz (BAĞLANDI→Bağlan→BAĞLANDI titremesi ve "tıklama
                // yok sayıldı" hissi bu yüzden oluşuyordu; aradaki tıklamalar da
                // kesme yerine YENİDEN BAĞLANMA tetikliyordu).
                _connectionStarting = true;
                Volatile.Write(ref _connectionState, false);
                // Disconnect: release capture and clear any transport override so
                // ModeOff maps to the clean (no TUN, no system proxy) default.
                connectionViewModel.Transport = "";
                DiagLog.Write("STATE ui disconnect (user toggle)");
                await SendConnectionStateAsync();
                // Kullanıcı kararını ÖNCE kalıcılaştır — ApplyCmd doğrulama/izin
                // kapısında düşse bile kalıcı mod bayat "GPN" olarak geri dönmesin.
                await PersistConnectionModeAsync(connectionViewModel, SplitTunnelViewModel.ModeOff);
                await ApplyConnectionModeAsync(connectionViewModel, SplitTunnelViewModel.ModeOff);

                // VPN oturum günlüğünü kapat (aktif değilse no-op — GPN bağlantı
                // kesmesi VPN bloğunu etkilemez).
                VpnSessionLog.EndSession("VPN disconnect");
                DiagLog.Write("VPN_LOG disconnect");

                // Teardown tamamlandı: geçiş bayrağını kapat ve GERÇEK durumu yayınla.
                // Supervisor artık "bağlı" yayınlayabilir — ama teardown bittiği için
                // gerçek durum zaten kesiktir (tutarlı, tek yönlü geçiş).
                _connectionStarting = false;
                await SynchronizeConnectionStateAsync(forcePublish: true);
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

                // Kullanıcı kararı önce kalıcılaşır ve yayınlanır: geçiş sürerken
                // bile eski mod echoları ("GPN'e geri dönüyor" algısı) yeni seçimi
                // ezemez; sonraki Reload'lar da aynı kararla akışı sürdürür.
                await PersistConnectionModeAsync(connectionViewModel, targetMode);

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
                DiagLog.Write($"STATE ui connect settled (user toggle) connected={ReadActualConnectionState()} mode={requestedMode}");
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
        }
    }

    /// <summary>
    /// Belirgin "GPN Bağlan" butonu: GPN modu açıkça seçildiğinde seçili profil
    /// WireGuard olmasa bile İtalya/Almanya otomatik seçimini zorla tetikler
    /// (ToggleConnectionAsync'ın seçili-profile bağımlı dalı yerine doğrudan
    /// ViewModel koordinatörünü kullanır). Bağlantı kesme, çekirdek geçişini
    /// temizler.
    /// </summary>
    public Task RunGpnConnectAsync()
        => _commandGate.RunAsync("gpn_connect", RunGpnConnectCoreAsync);

    private async Task RunGpnConnectCoreAsync()
    {
        try
        {
            DiagLog.Write($"STATE ui GPN connect requested actualConnected={ReadActualConnectionState()}");

            // Kurtarma penceresinde yeni bağlantı isteği görmezden gelinir (buton
            // zaten kilitli — tray/hotkey için koruma).
            if (Volatile.Read(ref _autoRecovering) == 1)
            {
                DiagLog.Write("STATE ui GPN connect ignored — auto-recovery in progress");
                return;
            }

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
                // GPN butonu bağlıyken ikinci basış = toggle-kesme: koordinatör
                // Disconnected'a geçti, çekirdek durduruldu — "çekirdek Ready olana
                // dek bekle" burada anlamsız (Stopped durumuna asla Ready demez ve
                // 20 sn boşa dönerdi). Mod değişimi/bağlanma akışlarında koordinatör
                // Disconnected'a GEÇMEZ, bu yüzden bu erken-çıkış yalnızca kesmeyi
                // etkiler; GPN→VPN geçişi beklemesini bozmaz.
                if (ViewModel?.GpnCoordinatorSnapshot is { State: GpnConnectionState.Disconnected })
                {
                    DiagLog.Write("STATE ui GPN toggle-disconnect settled — core wait skipped");
                }
                else
                {
                    await WaitForCoreLeavingStartingAsync();
                }
            }
            _connectionStarting = false;
            await SynchronizeConnectionStateAsync(forcePublish: true);
            DiagLog.Write($"STATE ui GPN connect settled connected={ReadActualConnectionState()}");
            // Bağlantı kurulamadıysa (koordinatör Failed / çekirdek Failed) nedeni
            // gösteren hata kartını dashboard'a bas.
            await TryPushConnectionFailureAsync();
        }
        finally
        {
            _connectionStarting = false;
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
    public Task SetConnectionModeAsync(string mode)
        => _commandGate.RunAsync("set_mode", () => SetConnectionModeCoreAsync(mode));

    private async Task SetConnectionModeCoreAsync(string mode)
    {
        try
        {
            var connectionViewModel = ViewModel?.ConnectionViewModel;
            if (connectionViewModel is null || !ReadEffectiveConnectionState())
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

            // Karar ÖNCE kalıcılaşır ve yayınlanır: geçiş sürerken bile bayat eski
            // mod echoları ("sürekli GPN'e geri dönüyor" algısı) kullanıcı seçimini
            // ezemez; ApplyCmd doğrulama/izin kapısında düşse bile kalıcı mod doğrudur.
            _connectionStarting = true;
            await PersistConnectionModeAsync(connectionViewModel, targetMode);
            await ApplyConnectionModeAsync(connectionViewModel, targetMode);
            // Geçişi çekirdek oturana dek "bağlanıyor"da tut; sonra gerçek durumu yayınla.
            await WaitForCoreLeavingStartingAsync();
            _connectionStarting = false;
            await SynchronizeConnectionStateAsync(forcePublish: true);
            // Geçiş başarısızsa (GPN seçim/launcher hatası, çekirdek yok) neden kartı.
            await TryPushConnectionFailureAsync();
        }
        finally
        {
            _connectionStarting = false;
        }
    }

    /// <summary>
    /// Applies the three-state split-routing mode exposed by the Game Boost view.
    /// Unlike the legacy mode switch, this also accepts "off" while disconnected so
    /// the dashboard can stage a complete routing policy before connecting.
    /// </summary>
    public Task SetDashboardModeAsync(string mode)
        => mode is not ("off" or "vpn" or "manual")
            ? Task.CompletedTask
            : _commandGate.RunAsync("set_split_mode", () => SetDashboardModeCoreAsync(mode));

    private async Task SetDashboardModeCoreAsync(string mode)
    {
        var connectionViewModel = ViewModel?.ConnectionViewModel;
        if (connectionViewModel is null)
        {
            return;
        }

            if (mode == "off")
            {
                connectionViewModel.Transport = "";
                // Karar önce kalıcılaşır — bağlıyken "kesme" isteği, bağlantısızken
                // "başlamadan hazırla" aynı yoldan döner ve bayat mod birikmez.
                await PersistConnectionModeAsync(connectionViewModel, SplitTunnelViewModel.ModeOff);
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
                await PersistConnectionModeAsync(connectionViewModel, targetMode);
                await ApplyConnectionModeAsync(connectionViewModel, targetMode);
            }

        await PushMonitorSnapshotAsync(force: true);
    }

    /// <summary>
    /// Switches the capture transport between TUN and the system proxy, mirroring the
    /// native status bar's EnableTun + SysProxyType toggles. While connected the change
    /// is applied immediately; while disconnected it is remembered for the next connect.
    /// </summary>
    public Task SetTransportAsync(string transport)
        => _commandGate.RunAsync("set_transport", () => SetTransportCoreAsync(transport));

    private async Task SetTransportCoreAsync(string transport)
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

    /// <summary>
    /// GPN koordinatörü canlıyken (soft geçiş sonrası — koordinatör Connected) veya
    /// zaten "bağlı" olarak yayınlanmış bir durum varken gerçek bir bağlantı vardır.
    /// Kesme kararı bu üç kaynağı birden okur; yalnızca çekirdek sağlığına dayanmak,
    /// 2 sn'lik poll'un bayat "bağlı değil" görüşünün kesmeyi "bağlan"a çevirmesine
    /// yol açardı (bağlantı kesme butonunun yanıt vermemesi).
    /// </summary>
    private bool ReadEffectiveConnectionState()
        => ReadActualConnectionState() || IsGpnTunnelActive() || Volatile.Read(ref _connectionState);

    /// <summary>
    /// Kullanıcının bağlantı modu kararını ÖNCE kalıcılaştırır, ViewModel bayrağını
    /// sessizce eşitler ve gerçek durumu yayınlar. Amaç: ApplyCmd doğrulama/izin
    /// kapısında düşse bile (kural kaydı hatası, TUN yüksekliği vb.) kalıcı mod bayat
    /// "eski seçim"de kalmasın — dashboard sonraki poll/echo'da kullanıcının yaptığı
    /// seçimi gösterir ("sürekli GPN'e geri dönüyor" algısının kaynağı budur).
    /// </summary>
    private async Task PersistConnectionModeAsync(
        SplitTunnelViewModel connectionViewModel,
        int targetMode)
    {
        var config = AppManager.Instance.Config;
        config.ConnectionItem ??= new();
        config.ConnectionItem.Mode = targetMode;
        config.ConnectionItem.Transport = targetMode == SplitTunnelViewModel.ModeOff
            ? ""
            : connectionViewModel.Transport is "tun" or "proxy" ? connectionViewModel.Transport : "";
        await ConfigSaveQueue.SaveAndWaitAsync(config);
        // VM bayrağı sessizce eşitle — ApplyCmd aşağıda hemen uygulayacağı için
        // mod-değişim aboneliğinin gecikmeli otomatik uygulamasını tetikleme.
        connectionViewModel.SetModeSilently(targetMode);
        await SendConnectionStateAsync();
    }

    private async Task ApplyConnectionModeAsync(
        SplitTunnelViewModel connectionViewModel,
        int targetMode)
    {
        WarnOnForeignTunnelBeforeConnect(targetMode);
        // Modu SESSİZCE eşitle, ardından ApplyCmd hemen uygular. Doğrudan `Mode =`
        // atanırsa mod aboneliği +800ms'lik debounce'lu İKİNCİ bir auto-apply planlar;
        // o ikinci apply, az önce kurulan tüneli tekrar yıkabilen fazladan bir
        // ReloadRequested yayınlardı (VPN→GPN "bazen geçiyor bazen geçmiyor" +
        // çift ConnectAsync yarışının kaynağı).
        connectionViewModel.SetModeSilently(targetMode);
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

        // Kullanıcı bağlantıyı KESTİ (ModeOff): GPN tüneli "sıcak" kalmamalı.
        // Yumuşak uygulayıcı Off vektörünü canlı mihomo'ya yazar (trafik DIRECT)
        // ama Wintun/TUN adaptörünü, failover izleyicisini ve superset oturumu
        // yerinde bırakır — "Bağlantıyı Kes" gerçek bir teardown olmalı. Aşağıdaki
        // proxy-only reconcile, tünel gerçekten inince devreye girer (aynı porta
        // ikinci çekirdek çakışması olmaz).
        if (targetMode == SplitTunnelViewModel.ModeOff && ViewModel is { } gpnTeardownVm)
        {
            await gpnTeardownVm.StopGpnTunnelIfActiveAsync();
        }

        // Publish the effective state after ApplyCmd has completed. If validation or
        // elevation rejected the change, the persisted mode/core state remains unchanged.
        await SynchronizeConnectionStateAsync(forcePublish: true);

        // After a disconnect (or a rejected mode change back to Off) the proxy-only
        // core may need to come back up so the system proxy keeps working. Kullanıcı
        // kesmesinde GPN tüneli yukarıda (StopGpnTunnelIfActiveAsync) gerçekten
        // indirilir; yumuşak mod geçişleri (örn. GPN↔VPN) çekirdeği reload ile
        // değiştirir. Tünel canlıysa proxy-only çekirdek başlatılmaz (aynı porta
        // ikinci çekirdek çakışırdı).
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
            || AppManager.Instance.IsRunningCore(ECoreType.mihomo)
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
            DiagLog.Write("STATE pre-connect foreign scan clean — no competing tunnel/proxy state");
            return;
        }

        DiagLog.Write("STATE pre-connect foreign state: "
            + $"tun=[{string.Join(",", result.TunAdapterNames)}] "
            + $"processes=[{string.Join(",", result.ForeignProcessNames)}] "
            + $"portConflict={result.HasPortConflict}");

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

        // Resmi WireGuard istemcisinin (WireGuard for Windows) AKTİF tüneli, mihomo
        // TUN'un default rotasıyla yarışır ve WFP filtreleriyle çakışır — kopma/
        // kararsızlığın bilinen kaynağı. Ayrı uyarıyla açıkça söyle (isteyen kapatsın;
        // AoGPN üçüncü taraf istemcileri asla kapatmaz).
        var activeWgAdapters = result.TunAdapterNames
            .Where(n => n.Contains("wireguard", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (activeWgAdapters.Length > 0)
        {
            var wgNames = string.Join(", ", activeWgAdapters);
            DiagLog.Write($"STATE pre-connect WireGuard-for-Windows tunnel ACTIVE ({wgNames}) — competing default route can make the new TUN unstable");
            NoticeManager.Instance.SendMessageAndEnqueue(string.Format(
                ResUI.ForeignTunnelWarning,
                string.Join("; ", details.Concat([$"Aktif WireGuard istemcisi tüneli: {wgNames} — kapatılmadan bağlanılırsa yeni TUN ile rota çakışması olur ve bağlantı kopabilir."]))));
        }
        else
        {
            NoticeManager.Instance.SendMessageAndEnqueue(string.Format(
                ResUI.ForeignTunnelWarning, string.Join("; ", details)));
        }
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
            if (actualState != previousState)
            {
                // Zaman çizelgesi: telemetri tick'i gerçek durumu değiştirdiğini
                // gördü — hangi sağlık durumuyla (Ready dışı = çekirdek yok/
                // çöktü) yayınlandığını kaydet.
                var healthState = AppManager.Instance.CoreEngineHost
                    ?.GetHealth(CoreHealthRole.Main)?.State;
                DiagLog.Write($"STATE ui telemetry flip connected={actualState} (was {previousState}) health={healthState?.ToString() ?? "?"} autoRecovering={Volatile.Read(ref _autoRecovering) == 1}");
            }
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
                DiagLog.Write($"STATE ui connect wait aborted after {(int)sw.Elapsed.TotalMilliseconds} ms");
                return;
            }

            // GPN koordinatörü başarısız olduysa (seçim/launcher hatası — çekirdek
            // hiç başlamamış olabilir) hazır-beklemeye girmeyelim: 20 sn yerine hata
            // kartı hemen yayınlanır.
            if (ViewModel?.GpnCoordinatorSnapshot is { State: GpnConnectionState.Failed })
            {
                DiagLog.Write($"STATE ui connect wait aborted (coordinator Failed) after {(int)sw.Elapsed.TotalMilliseconds} ms");
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
                // settled: connected, degraded, or failed — publish as-is.
                DiagLog.Write($"STATE ui connect wait settled health={health.State} after {(int)sw.Elapsed.TotalMilliseconds} ms");
                return;
            }
            await Task.Delay(200);
        }
        DiagLog.Write($"STATE ui connect wait TIMEOUT after {timeoutSeconds} s — publishing as-is");
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
                    // Executing a disabled ReactiveCommand is a no-op, so the
                    // CanExecute gate (bound to the reload enablement state) is
                    // respected implicitly.
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

    // All theme ids the dashboard can apply (mirrors Temalar/themes.json). Themes
    // without a WPF palette never appear in TryMapWebThemeToWpf but must still be
    // accepted as a persisted DashboardTheme so the startup push survives restarts.
    private static readonly HashSet<string> DashboardThemeIds = new(StringComparer.Ordinal)
    {
        "nebula", "inferno", "venom", "cryo", "synthwave", "cyberpunk", "matrix",
        "plasma", "phantom", "crimson", "velocity", "aurora", "candy", "obsidian",
        "sandstorm", "neon-cyber", "titanium-orange", "matrix-green", "ocean-blue",
        "red-phantom", "violet-nova", "arctic-ice", "gold-elite", "stealth-camo",
        "crimson-core",
    };

    /// <summary>
    /// True when the id names one of the 25 shipped dashboard themes (mirrors
    /// Temalar/themes.json). Used to validate renderer set_theme payloads before
    /// they are persisted or interpolated into an ExecuteScriptAsync call.
    /// </summary>
    internal static bool IsKnownDashboardTheme(string themeId) => DashboardThemeIds.Contains(themeId);

    /// <summary>
    /// Maps a native WPF theme (an <see cref="ETheme"/> name) to its dashboard
    /// theme id. The reverse of <see cref="TryMapWebThemeToWpf"/>; unknown themes
    /// map to the default "nebula" palette.
    /// </summary>
    internal static string MapWpfThemeToWeb(string wpfTheme)
    {
        return wpfTheme switch
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
    }

    /// <summary>
    /// Resolves the dashboard theme for the startup push: the persisted raw theme
    /// id (any of the 25, including the WPF-less 14) wins when it is a known id;
    /// otherwise the value is derived from the native <see cref="ETheme"/>.
    /// </summary>
    internal static string ResolveInitialDashboardTheme()
    {
        var stored = AppManager.Instance.Config.UiItem.DashboardTheme;
        if (stored is not null && DashboardThemeIds.Contains(stored))
        {
            return stored;
        }

        var wpfTheme = AppManager.Instance.Config.UiItem.CurrentTheme ?? nameof(ETheme.Dark);
        return MapWpfThemeToWeb(wpfTheme);
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
        // Defter: Connecting/Connected eski başarısızlığı temizler, Failed saklar.
        _failureLedger.RecordGpnSnapshot(snapshot);

        switch (snapshot.State)
        {
            case GpnConnectionState.Connecting:
                break;
            case GpnConnectionState.Connected:
                Volatile.Write(ref _connectionState, true);
                _activeGpnServer = snapshot.Server;
                DiagLog.Write($"STATE ui gpn snapshot Connected (server={snapshot.Server?.Name ?? "-"} summary={snapshot.ProfileSummary})");
                await SendConnectionStateAsync();

                // Gerçek ping "sonra" ölçümü: tünel kurulunca aynı uç noktalara
                // tünel yolundan ping atılır ve açılıştaki "önce" değeriyle
                // karşılaştırılır (öncesi/sonrası).
                //
                // A4: yalnızca oturum başına BİR kez ve tünel oturduktan SONRA
                // koşar — adaptör/rota yerleşirken ve yumuşak geçişte tekrar
                // tetiklenmez; aksi halde her düğüm değişiminde oyun uç noktalarına
                // ping yağar (il ölçümün "önce" değeriyle karşılaştırması da bozulur).
                if (!_realPingAfterMeasured)
                {
                    _realPingAfterMeasured = true;
                    _ = MeasureRealPingAsync(isBefore: false, delay: TimeSpan.FromSeconds(10));
                }
                break;
            case GpnConnectionState.Disconnected:
                Volatile.Write(ref _connectionState, false);
                _activeGpnServer = null;
                _realPingAfterMeasured = false;
                DiagLog.Write("STATE ui gpn snapshot Disconnected");
                // Geçiş (kesme / mod değişimi) SIRASINDA koordinatör teardown'u
                // "koptu" yayınlamasın ve geçiş akışının bayrağını bozmasın:
                // GPN→VPN geçişinde StopGpnCoordinatorWhenNotConnectedAsync
                // koordinatörü Disconnected yapar; o anda yayınlanan kesik durum
                // bağlandı→koptu→bağlandı titremesi üretiyordu. Yerleşik durumu
                // geçiş akışı yayınlar; bekleyen WaitForCoreLeavingStartingAsync
                // koordinatör durumunu kendisi izler ve erken döner.
                if (Volatile.Read(ref _connectionStarting))
                {
                    break;
                }
                await SendConnectionStateAsync();
                break;
            case GpnConnectionState.Failed:
                break;
        }
    }

    /// <summary>
    /// Çekirdek sağlık olaylarını dashboard bağlantı durumuna çevirir. Kritik dal:
    /// CoreManager beklenmedik bir çıkışta otomatik kurtarma başlattığında (Degraded
    /// + <see cref="CoreHealthSnapshot.Recovering"/>) durum "kopmuş/Bağlan" yerine
    /// "yeniden bağlanıyor" olarak yayınlanır — buton kilitli kalır ve çekirdek
    /// Ready dönünce bağlantı kullanıcı tıklaması OLMADAN "bağlı"ya döner. Böylece
    /// kullanıcının gördüğü "bağlandı → Bağlan → 3-5 sn sonra kendiliğinden bağlandı"
    /// yanıp sönmesi, otomatik kurtarmanın şeffaf göstergesine dönüşür.
    /// </summary>
    private async Task OnMainCoreHealthChangedAsync(CoreHealthSnapshot health)
    {
        var wasRecovering = Volatile.Read(ref _autoRecovering) == 1;
        switch (health.State)
        {
            case CoreHealthState.Degraded when health.Recovering && !wasRecovering:
                // Kurtarma başladı: durumu "bağlanıyor (otomatik yeniden bağlanma)"
                // olarak yayınla — gerçek bir kesinti bildirimi için Ready/Failed
                // terminal durumunu bekle.
                Volatile.Write(ref _autoRecovering, 1);
                Volatile.Write(ref _connectionState, false);
                DiagLog.Write("STATE ui auto-recovery began — publishing connecting");
                try
                {
                    // Kendiliğinden yeniden bağlanmanın kullanıcıya görünür nedeni:
                    // kısa "Yeniden bağlanıyor" anonsu — kopma asla sessiz geçmez.
                    NoticeManager.Instance.SendMessageEx("Bağlantı koptu — otomatik yeniden bağlanıyor…");
                }
                catch
                {
                    // Bildirim kanalı best-effort; durum yayını asla engellenmez.
                }
                await SendConnectionStateAsync();
                break;
            case CoreHealthState.Ready when wasRecovering:
                Volatile.Write(ref _autoRecovering, 0);
                Volatile.Write(ref _connectionState, true);
                DiagLog.Write("STATE ui auto-recovery finished ok — publishing connected");
                await SendConnectionStateAsync();
                break;
            case CoreHealthState.Failed when wasRecovering:
                // Kurtarma denemeleri tükendi: ancak şimdi gerçek kopma + neden kartı.
                Volatile.Write(ref _autoRecovering, 0);
                Volatile.Write(ref _connectionState, false);
                DiagLog.Write($"STATE ui auto-recovery exhausted — publishing disconnected (error={health.Error})");
                await SendConnectionStateAsync();
                await TryPushConnectionFailureAsync();
                break;
            case CoreHealthState.Stopped when wasRecovering:
                // İstemli durdurma kurtarmayı iptal etti — normal kesme akışı durumu
                // yayınlar; burada yalnızca bayrağı kapat.
                Volatile.Write(ref _autoRecovering, 0);
                break;
            case CoreHealthState.Failed:
                // Kurtarmasız doğrudan Failed (AutoReconnect kapalı ya da deneme
                // limiti doldu): oturum ortasında kopan bağlantının nedeni hata
                // kartına düşsün (bağlantı-kurma akışının sonundaki çağrıyla aynı
                // defter — tekrar zararsızdır).
                await TryPushConnectionFailureAsync();
                break;
        }
    }

    private async Task SendConnectionStateAsync()
    {
        var connectedJson = JsonSerializer.Serialize(Volatile.Read(ref _connectionState));
        var connectingJson = JsonSerializer.Serialize(_connectionStarting
            || Volatile.Read(ref _autoRecovering) == 1);
        var configuredMode = AppManager.Instance.Config.ConnectionItem?.Mode;
        // Push only a real routing mode. When the persisted mode is Off (or never
        // configured), send "off" so the dashboard keeps its own remembered
        // gpn/vpn choice (localStorage) instead of being forced to VPN — the
        // first-run default stays GPN.
        var modeJson = JsonSerializer.Serialize(configuredMode switch
        {
            SplitTunnelViewModel.ModeManual => "gpn",
            SplitTunnelViewModel.ModeVpn => "vpn",
            _ => "off",
        });
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
    private Task NotifyConnectionErrorAsync(string message) => _failureLedger.PushErrorAsync(message);

    /// <summary>
    /// Bağlanma denemesi başarısız olduysa dashboard'a hata kartı bastırır
    /// (ConnectionFailureLedger — kayıtlar, GPN önceliği ve 45 sn tazelik orada).
    /// </summary>
    private Task TryPushConnectionFailureAsync() => _failureLedger.TryPushAsync();

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

            // Native (in-process) motor: WinDivert tabanlı UDP-odaklı beyaz liste
            // tünelidir — bu IP doğrulaması dahil uygulamanın TCP istekleri TASARIM
            // gereği doğrudan gider (yalnızca hedef uygulamaların UDP'si tünellenir).
            // "Doğrudan IP, tünel IP'sinden farklı değil" karşılaştırması bu modda
            // geçerli bir sızıntı kanıtı DEĞİLDİR ve yanlış "Sızıntı" uyarısı üretir
            // (canlı gözlenen banner). Doğrulama = veri düzleminin ÇİFT YÖNLÜ paket
            // taşıdığı (sunucuya gönderilen VE sunucudan çözülen > 0). Tek yönlü sayaç
            // (ör. sent=104 / received=1 — sunucunun yanıt vermediği yarı-ölü oturum)
            // "Tünellendi ✓" iddiasını DOĞRULAMAZ; yalnızca handshake/keepalive
            // gürültüsü olabilir. İki yön de akıyorsa veri yolu gerçekten çalışıyordur.
            var nativeBridge = NativeGpnEnginePolicy.IsEnabled ? AppManager.Instance.CaptureBridge : null;
            if (nativeBridge is { IsRunning: true }
                && nativeBridge.TunnelSnapshot.Sent > 0
                && nativeBridge.TunnelSnapshot.Received > 0)
            {
                tunnelVerified = true;
                DiagLog.Write($"GPN_IPVERIFY native data-plane verified sent={nativeBridge.TunnelSnapshot.Sent} received={nativeBridge.TunnelSnapshot.Received}");
            }
            else if (connected && transport == "proxy")
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
                //
                // Kontaminasyon uyarısı: GPN superset oturumunda doğrulama hostları
                // (ipify/ip.sb/ipinfo/ip-api) kural gereği GPN-CHECK → WG tüneline
                // gider; TUN/system proxy etkinken "direct" bacağı da aynı tünelden
                // çıkar → direct==tunnel olur ve gerçek tünel çalışsa bile yanlış
                // "Sızıntı" alarmı üretilirdi (canlı gözlenen: direct=92.4.220.236
                // tunnel=92.4.220.236 verified=False). Böyle durumda tünel IP'si
                // önbellekteki ISP baz çizgisiyle karşılaştırılır (TUN modundakiyle
                // aynı mantık). Gerçek sızıntıda tünel yoktur → tunnelIp==directIp
                // == ISP olur ve sonuç yine false kalır.
                var directContaminated = tunnelIp is { Length: > 0 }
                    && string.Equals(tunnelIp, directIp, StringComparison.OrdinalIgnoreCase);
                tunnelVerified = tunnelIp is { Length: > 0 }
                    && (!string.Equals(tunnelIp, directIp, StringComparison.OrdinalIgnoreCase)
                        || (directContaminated
                            && _realIspIp is { Length: > 0 }
                            && !string.Equals(tunnelIp, _realIspIp, StringComparison.OrdinalIgnoreCase)));
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

            // Düğüm ülkesi vs çıkış ülkesi: seçili düğümün ülkesi (remark/GeoIP)
            // ile tünel egress ülkesi karşılaştırılır. Farklıysa (örn. düğüm
            // Almanya, egress İtalya) köprüleme YOKTUR — sunucunun çıkış ağı
            // farklı ülkede görünüyor; dashboard bunu amber uyarıyla gösterir.
            // Önce oturum düğümü (launcher'ın gerçekten yüklediği WG profili),
            // yoksa varsayılan düğüm kullanılır.
            string? nodeCountry = null;
            if (connected)
            {
                // WireGuard oturumunda beklenen çıkış ülkesi, tünelin SONLANDIĞI
                // sunucunun uç noktasıdır; mihomo'nun seçili düğümü (GPN-Nodes
                // grubu) AYRI bir katmandır. Eskiden düğüm ülkesi karşılaştırılıyordu
                // ve yumuşak geçiş sonrası / grup üzerinden gitmeyen rotalarda her
                // oturumda yanlış "ülke uyuşmuyor" uyarısı çıkıyordu (canlı log:
                // düğüm Almanya, çıkış İtalya). Artık uyarı yalnızca GERÇEK bir
                // anomali — çıkış IP'si tünel sunucusundan farklı ülkede — görünür.
                if (_activeGpnServer is { } gpnServer && gpnServer.EndpointHost.IsNotEmpty())
                {
                    nodeCountry = DashboardMessageDispatcher.ResolveNodeCountry(null, gpnServer.EndpointHost);
                }
                else
                {
                    var sessionNode = ServiceLib.Services.Gpn.GpnSoftSession.Node;
                    sessionNode ??= await ServiceLib.Handler.ConfigHandler.GetDefaultServer(AppManager.Instance.Config);
                    if (sessionNode is not null)
                    {
                        nodeCountry = DashboardMessageDispatcher.ResolveNodeCountry(sessionNode.Remarks, sessionNode.Address);
                    }
                }
            }
            var exitMismatch = nodeCountry.IsNotEmpty()
                && tunnelCountry.IsNotEmpty()
                && !string.Equals(nodeCountry, tunnelCountry, StringComparison.OrdinalIgnoreCase);

            // Ölçümü oturum günlüğüne de yaz — "IP değişmedi / sızıntı" belirtileri
            // canlı oturumda görülemeden gpn-session.log'dan tanımlanabilsin.
            DiagLog.Write($"GPN_IPVERIFY connected={connected} transport={transport} "
                + $"direct={directIp} tunnel={tunnelIp ?? ""} isp={_realIspIp ?? ""} "
                + $"verified={tunnelVerified} ispCached={_ispIpCached} "
                + $"nodeCountry={nodeCountry ?? ""} exitMismatch={exitMismatch}");

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
                    nodeCountry = nodeCountry ?? "",
                    exitMismatch,
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
    public Task PushAppIconsAsync(string[] paths) => _pushService.PushAppIconsAsync(paths);
    /// <summary>Wave 3: <see cref="DashboardPushService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task PushMonitorSnapshotAsync(bool force = false) => _pushService.PushMonitorSnapshotAsync(force);

    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task SelectNodeAsync(string indexId) => _nodeService.SelectNodeAsync(indexId);

    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task CopyNodesAsync(string[] indexIds) => _nodeService.CopyNodesAsync(indexIds);
    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task PasteNodesAsync() => _nodeService.PasteNodesAsync();
    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task EditNodeAsync(string indexId) => _nodeService.EditNodeAsync(indexId);
    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task ImportWireGuardConfsAsync(List<WireGuardConfFile> files) => _nodeService.ImportWireGuardConfsAsync(files);
    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task DeleteNodesAsync(string[] indexIds) => _nodeService.DeleteNodesAsync(indexIds);
    /// <summary>Wave 3: <see cref="DashboardNodeService"/> delegasyonu — gövde servise taşındı.</summary>
    public Task MoveNodeAsync(string indexId, string targetIndexId) => _nodeService.MoveNodeAsync(indexId, targetIndexId);

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

    /// <summary>
    /// Host-composed undo-toast push: the dispatcher serializes the payload
    /// (window.setUndoAvailable({ kind, processName, displayName }) or null)
    /// and this member only executes it on the ready WebView.
    /// </summary>
    public async Task PushUndoStateAsync(string script)
    {
        if (!_webViewReady)
        {
            return;
        }
        await ExecuteScriptSafelyAsync(script);
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
    /// <summary>W4-A: <see cref="ConnectionLifecycleSupervisor"/> delegasyonu — döngü gövdesi servise taşındı.</summary>
    private void StartTelemetryLoop() => _lifecycleSupervisor.Start(_webViewLifetime.Token);

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

        // ShouldTouch: kapanmış denetleyicide CoreWebView2 null OLMAZ, yalnızca
        // üyeleri InvalidOperationException atar; bu yüzden yıkım bir kez
        // gözlendiğinde kilit kalıcı olarak kapanır ve 2 sn'lik poll aynı hatayı
        // her turda yeniden üretmeyi bırakır.
        if (!_webViewLatch.ShouldTouch(_isClosing, _webViewReady, WebView.CoreWebView2 is not null))
        {
            return;
        }

        try
        {
            if (WebView.CoreWebView2.IsSuspended)
            {
                // The dashboard is frozen (window minimized to the tray); nothing to
                // push until Resume() runs, and script execution is not allowed
                // while suspended.
                return;
            }

            await _dashboardHost.ExecuteScriptAsync(script);
        }
        catch (Exception ex) when (
            ex is COMException
            or InvalidOperationException
            or ObjectDisposedException)
        {
            // WebView2 can be torn down concurrently with a timer tick during close.
            // Yıkım hatası gözlendi: kalıcı kilidi kapat (dg. "controller was closed").
            _webViewLatch.CloseOnDestruction();
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

        // Son çare ağı: hangi yoldan kapanırsa kapansın (reboot-as-admin,
        // AppExitAsync doğrudan çağrısı vb.) GPN koordinatörü best-effort durdurulur.
        // ExitApplicationSafelyAsync / SessionEnding zaten bekleyerek kapatır; bu yol
        // yalnızca kaçış yollarını kapsar — OnExit'in 6 sn'lik tırmanıcısı asılı
        // kalırsa süreci sonlandırır.
        if (ViewModel is { } closedVm)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await closedVm.StopGpnTunnelIfActiveAsync();
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("GPN coordinator stop on close failed", ex);
                }
            });
        }

        _dashboardHost.WebMessageReceived -= _dashboardMessageDispatcher.HandleWebMessageReceived;
        _dashboardHost.NavigationCompleted -= CoreWebView2_NavigationCompleted;
        try
        {
            _dashboardHost.DisposeAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // WebView2 teardown COMException'leri (0x8007139F vb.) kapatma
            // yolundan çıkıp WPF'i unhandled-exception ile çökertmesin
            // (debugger'da exit code 0xffffffff olarak görünür).
            Logging.SaveLog("AoGPN dashboard host dispose on close failed", ex);
        }
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
        // MainSnackbar, WebView2 yerleşiminde kalıcı olarak daraltılmış legacy
        // DialogHost'un içinde yaşar ve hiç yüklenmez — kuyruğa bağlı Snackbar
        // örneği olmadan Enqueue her mesajda NLog'a "snackbar instances are not
        // assigned" uyarısı yazar ve mesaj zaten hiç görüntülenmez. Yalnızca
        // Snackbar gerçekten bağlandığında (legacy yüzey görünürken) kuyruğa al.
        if (!MainSnackbar.IsLoaded)
        {
            return;
        }
        MainSnackbar.MessageQueue?.Enqueue(content);
        await Task.CompletedTask;
    }

    private async Task DelegateSnackAction(ActionNotice notice)
    {
        if (!MainSnackbar.IsLoaded)
        {
            return;
        }
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
        if (!_webViewLatch.ShouldTouch(_isClosing, _webViewReady, WebView.CoreWebView2 is not null))
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
            // Yıkım hatası gözlendi: kalıcı kilidi kapat.
            _webViewLatch.CloseOnDestruction();
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
            // GPN koordinatörü düzenli kapanmadan çıkılmamalı: izleyici görevi,
            // yakalama köprüsü ve superset oturum kaydı ancak launcher teardown'ında
            // temizlenir. Aksi halde çıkış sırasında arka planda kalan görevler ve
            // Wintun/TUN durumu kararsız kapanışa (asılı OnExit, gizli tünel) yol açar.
            if (ViewModel is { } exitVm)
            {
                await exitVm.StopGpnTunnelIfActiveAsync();
            }
            await _trayBehavior.ExitApplicationAsync();

            // Kapanış kancası — hayalet Wintun adaptör süpürmesi: çekirdek artık
            // durdu (TrayWindowCoordinator "core stop" adımı). Teardown sırasında
            // silinememiş sahipsiz adaptörler (ör. mihomo kapanışı yarım kaldıysa)
            // burada son bir tur temizlenir — bu örneğin kendi adaptörü zaten
            // kapanmış olduğundan canlı adaptör silme riski yoktur. Best-effort:
            // yönetici yetkisi yoksa / wintun.dll yoksa kayıt düşülür, çıkış etkilenmez.
            try
            {
                await WintunOrphanSweeper.SweepOrphanedAsync(AppManager.Instance.Config?.GpnWintunItem);
            }
            catch (Exception ex)
            {
                Logging.SaveLog("AoGPN exit Wintun orphan sweep failed", ex);
            }
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
        // Oturum kapanışında da GPN tüneli düzenli teardown edilir — StopCoreAsync
        // yalnızca çekirdeği durdurur; koordinatör (izleyici + superset oturum +
        // yakalama köprüsü) bu çağrıyla temizlenir.
        if (ViewModel is { } sessionEndVm)
        {
            await sessionEndVm.StopGpnTunnelIfActiveAsync();
        }
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

    #endregion Event

    #region UI

    private void SetActiveNav(Button active)
    {
        foreach (var button in new[]
        {
            btnNavMsg, btnNavImport, btnNavScan,
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
            0 => btnNavMsg,
            1 => btnNavProxies,
            2 => btnNavConnections,
            3 => btnNavConnection,
            _ => null,
        };

        if (button is not null)
        {
            SetActiveNav(button);
        }
    }

    /// <summary>
    /// Makes the startup window visible once the WebView2 dashboard has loaded
    /// and been seeded (App.OnStartup parks the window off-screen so the raw
    /// empty frame never flashes — see <see cref="CoreWebView2_NavigationCompleted"/>).
    /// The splash closes at the same moment. Skipped while the window is
    /// minimized/hidden to the tray (hidden startup): the reveal then happens when
    /// the window is restored. Safe to call repeatedly.
    /// </summary>
    private void RevealStartupWindow()
    {
        if (!_dashboardFirstPaintPending || _isClosing)
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            // Hidden/minimized startup (AutoHideStartup) — stay pending; revealed
            // on restore. The splash must not linger, though: dismiss it now.
            App.Splash?.BeginCloseAsync();
            return;
        }

        _dashboardFirstPaintPending = false;

        // Finish the bar for the handoff (the status line is fixed — see
        // SplashWindow); the splash closes right below.
        App.Splash?.SetProgress(100);

        // Move the parked boot window (see App.OnStartup) to its saved spot — or
        // maximize it on first run — in the SAME tick the browser surface becomes
        // visible, so the first on-screen frame is the painted dashboard. No
        // opacity fade: WPF window opacity below 1 needs a layered window, and
        // layered windows paint solid black on machines with broken compositing
        // (the whole-screen black this off-screen boot exists to remove).
        ApplyStartupPlacement();
        DiagLog.Write($"WEBVIEW_BOOT reveal state={WindowState} "
            + $"left={Left:0} top={Top:0} w={ActualWidth:0} h={ActualHeight:0}");

        // The WebView2 dashboard is Visible from boot (see MainWindow.xaml) and
        // painted its frames while the window was parked off-screen; the line
        // below is a no-op safety net in case anything ever hides it again.
        WebView.Visibility = Visibility.Visible;

        // Measure the handoff end-to-end: reveal → first frame, so boot time is
        // fully traceable from ao_diag.txt (WEBVIEW_BOOT first-frame).
        _ = ProbeFirstFrameAsync();

        App.Splash?.BeginCloseAsync();
    }

    /// <summary>
    /// Logs the reveal → first-frame handoff latency and the total boot duration
    /// (splash → first dashboard frame) as a <c>WEBVIEW_BOOT first-frame</c> line.
    /// Two <c>requestAnimationFrame</c> callbacks ≈ the first composited frame
    /// after the reveal (the renderer has produced and delivered a frame to the
    /// compositor); ExecuteScriptAsync awaits the promise, so the measured delay
    /// is the renderer round-trip from the reveal tick. True DWM composition
    /// cannot be observed from inside the process without screen capture, so this
    /// is the closest host-side proxy. Bounded: a suspended (tray-hidden) or
    /// still-booting renderer must never hang anything — the probe gives up
    /// after 2 s and logs a timeout instead. Runs once per reveal (the reveal
    /// itself is gated by <see cref="_dashboardFirstPaintPending"/>).
    /// </summary>
    private async Task ProbeFirstFrameAsync()
    {
        try
        {
            var frameWatch = System.Diagnostics.Stopwatch.StartNew();
            var probe = WebView.CoreWebView2?.ExecuteScriptAsync(
                "new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => r('painted'))));");
            if (probe is null)
            {
                return;
            }

            var finished = await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(2)));
            var revealDelayMs = frameWatch.ElapsedMilliseconds;
            if (finished != probe)
            {
                DiagLog.Write("WEBVIEW_BOOT first-frame timeout "
                    + $"reveal-delay={revealDelayMs} ms (renderer suspended or page still booting)");
                return;
            }

            DiagLog.Write($"WEBVIEW_BOOT first-frame reveal-delay={revealDelayMs} ms "
                + $"splash-to-first-frame={(long)(DateTime.UtcNow - App.BootStartedAt).TotalMilliseconds} ms");
        }
        catch (Exception ex)
        {
            DiagLog.Write($"WEBVIEW_BOOT first-frame probe-failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Places the main window at the end of startup. Runs from
    /// <see cref="RevealStartupWindow"/>: the window is parked off-screen during
    /// boot (see App.OnStartup) and is only moved on-screen once the dashboard has
    /// painted, so the first frame the user sees is the finished UI. WindowBase
    /// defers the same placement during boot via
    /// <see cref="DeferPlacementUntilReveal"/>. Restores the saved placement;
    /// the first run (no saved placement yet) opens maximized so the dashboard
    /// fills the work area (see the note in <c>MainWindow_Loaded</c>).
    /// </summary>
    private void ApplyStartupPlacement()
    {
        ApplySavedPlacement();

        // First run only: no saved placement yet, so open maximized (the
        // work-area fill is the product look). Later runs restore the user's
        // last size/position/maximized state.
        if (ConfigHandler.GetWindowSizeItem(AppManager.Instance.Config, GetType().Name) is null)
        {
            WindowState = WindowState.Maximized;
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

            // A hidden-startup (AutoHideStartup / early minimize) that was never
            // revealed because the window was still minimized when the dashboard
            // finished booting becomes visible here.
            RevealStartupWindow();
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
        ViewModel.TabMainSelectedIndex = 3;
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
