using System.Windows.Media;
using AoGPN.Manager;
using MaterialDesignThemes.Wpf;

namespace AoGPN.Views;

public partial class StatusBarView
{
    private static Config _config;

    // Idle, proxy-only, connected.
    private static readonly Brush[] _trayStatusBrushes =
    [
        new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)),
        new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00)),
        new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
    ];

    public StatusBarView()
    {
        InitializeComponent();
        _config = AppManager.Instance.Config;

        menuExit.Click += menuExit_Click;
        txtRunningServerDisplay.PreviewMouseDown += txtRunningInfoDisplay_MouseDoubleClick;
        txtRunningInfoDisplay.PreviewMouseDown += txtRunningInfoDisplay_MouseDoubleClick;

        // The connection-mode radios read the persisted routing mode on every menu
        // open, so the tray always reflects the mode the dashboard last applied
        // (and the mode the tray itself requested via SetConnectionModeRequested).
        tbNotify.ContextMenu.Opened += (_, _) => RefreshConnectionModeDots();

        // Left click is wired in the constructor — NOT in WhenActivated — so the
        // handler is subscribed even if ReactiveUI activation never runs for this
        // view (a hidden-to-tray startup can skip it). TrayLeftMouseDown is raised
        // synchronously in the shell callback, the same delivery path as the
        // (working) right-click menu; TaskbarIcon.LeftClickCommand was dropped
        // because it fires from a System.Threading.Timer + Dispatcher.Invoke chain
        // that silently dies in some environments (elevated app + Efficiency Mode),
        // leaving the window impossible to restore. Double clicks raise the event
        // twice; NotifyLeftClickCmd's suppression collapses them to one toggle.
        tbNotify.TrayLeftMouseDown += TbNotify_TrayLeftMouseDown;

        this.WhenActivated(disposables =>
        {
            //tray live status line (text + colored dot)
            this.OneWayBind(ViewModel, vm => vm.TrayStatusLine, v => v.txtTrayStatus.Text).DisposeWith(disposables);
            this.WhenAnyValue(x => x.ViewModel.TrayStatusState)
                .Subscribe(state =>
                {
                    menuStatusIcon.Foreground = _trayStatusBrushes[Math.Clamp(state, 0, _trayStatusBrushes.Length - 1)];
                    // Quick-connect label flips with the live connection state.
                    txtQuickConnect.Text = state == 2 ? ResUI.TrayDisconnect : ResUI.TrayQuickConnect;
                })
                .DisposeWith(disposables);

            // Re-resolve the accent brushes when the WPF theme (dark/light palette)
            // changes so the active connection-mode dot follows the selected theme.
            AppEvents.ThemeChanged
                .AsObservable()
                .ObserveOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => RefreshConnectionModeDots())
                .DisposeWith(disposables);

            //tray quick actions and connection mode
            this.BindCommand(ViewModel, vm => vm.QuickConnectCmd, v => v.menuQuickConnect).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.TestServerCmd, v => v.menuTestServer).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.EnableTun, v => v.menuEnableTun.IsChecked).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ConnectionModeOffCmd, v => v.menuModeOff).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ConnectionModeVpnCmd, v => v.menuModeVpn).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ConnectionModeManualCmd, v => v.menuModeManual).DisposeWith(disposables);

            RefreshConnectionModeDots();

            //routings and servers
            this.OneWayBind(ViewModel, vm => vm.RoutingItems, v => v.cmbRoutings.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedRouting, v => v.cmbRoutings.SelectedItem).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.BlRouting, v => v.menuRoutings.Visibility).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.BlRouting, v => v.sepRoutings.Visibility).DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.Servers, v => v.cmbServers.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedServer, v => v.cmbServers.SelectedItem).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.BlServers, v => v.cmbServers.Visibility).DisposeWith(disposables);

            //tray menu
            this.BindCommand(ViewModel, vm => vm.AddServerViaClipboardCmd, v => v.menuAddServerViaClipboard2).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddServerViaScanCmd, v => v.menuAddServerViaScan2).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SubUpdateCmd, v => v.menuSubUpdate2).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.SubUpdateViaProxyCmd, v => v.menuSubUpdateViaProxy2).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.CopyProxyCmdToClipboardCmd, v => v.menuCopyProxyCmdToClipboard).DisposeWith(disposables);

            // Tray "Hide" only hides the window to the tray; the core and any
            // system proxy keep running. "Show" restores it. Tray "Exit"
            // (menuExit_Click) is the real quit that stops the core and clears
            // the proxy.
            this.BindCommand(ViewModel, vm => vm.ShowWindowCmd, v => v.menuShowWindow).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.HideWindowCmd, v => v.menuCloseTray).DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.RunningServerToolTipText, v => v.tbNotify.ToolTipText).DisposeWith(disposables);

            //status bar
            this.OneWayBind(ViewModel, vm => vm.InboundDisplay, v => v.txtInboundDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.InboundLanDisplay, v => v.txtInboundLanDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.RunningServerDisplay, v => v.txtRunningServerDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.RunningInfoDisplay, v => v.txtRunningInfoDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.SpeedProxyDisplay, v => v.txtSpeedProxyDisplay.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.SpeedDirectDisplay, v => v.txtSpeedDirectDisplay.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.EnableTun, v => v.togEnableTun.IsChecked).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.SystemProxySelected, v => v.cmbSystemProxy.SelectedIndex).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.RoutingItems, v => v.cmbRoutings2.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedRouting, v => v.cmbRoutings2.SelectedItem).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.BlRouting, v => v.cmbRoutings2.Visibility).DisposeWith(disposables);

            ViewModel.SetClipboardDataInteraction.RegisterHandler(interaction =>
            {
                var strData = interaction.Input;
                WindowsUtils.SetClipboardData(strData);
                interaction.SetOutput(Unit.Default);
            }).DisposeWith(disposables);

            ViewModel.DispatcherRefreshIconInteraction.RegisterHandler(interaction =>
            {
                Application.Current?.Dispatcher.Invoke(async () => await RefreshIcon(), DispatcherPriority.Normal);
                interaction.SetOutput(Unit.Default);
            }).DisposeWith(disposables);
        });

        _ = RefreshIcon();
    }

    private async Task RefreshIcon()
    {
        tbNotify.Icon = await WindowsManager.Instance.GetNotifyIcon(_config);
        Application.Current.MainWindow?.Icon = WindowsManager.Instance.GetAppIcon(_config);
    }

    private async void menuExit_Click(object sender, RoutedEventArgs e)
    {
        tbNotify.Dispose();
        await AppManager.Instance.AppExitAsync(true);
    }

    private void txtRunningInfoDisplay_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ViewModel?.TestServerAvailability();
    }

    private void TbNotify_TrayLeftMouseDown(object sender, RoutedEventArgs e)
    {
        // Runs synchronously on the shell callback — the same delivery path as the
        // working right-click menu (TaskbarIcon.LeftClickCommand's timer chain
        // silently died here). NotifyLeftClickCmd toggles the window. Uses the
        // singleton directly so it works regardless of WhenActivated/ViewModel timing.
        StatusBarViewModel.Instance.NotifyLeftClickCmd.Execute(Unit.Default).Subscribe();
    }

    /// <summary>
    /// Marks the active connection mode (Kapalı / VPN / Manuel) with a filled radio
    /// dot tinted by the theme accent; inactive modes get an empty, muted dot. The
    /// source of truth is the persisted routing mode (config.ConnectionItem.Mode),
    /// which the dashboard and the tray both write through the same apply path.
    /// </summary>
    private void RefreshConnectionModeDots()
    {
        var mode = _config.ConnectionItem?.Mode ?? SplitTunnelViewModel.ModeOff;
        SetConnectionModeDot(menuModeOffIcon, mode == SplitTunnelViewModel.ModeOff);
        SetConnectionModeDot(menuModeVpnIcon, mode == SplitTunnelViewModel.ModeVpn);
        SetConnectionModeDot(menuModeManualIcon, mode == SplitTunnelViewModel.ModeManual);
    }

    private static void SetConnectionModeDot(PackIcon icon, bool active)
    {
        icon.Kind = active ? PackIconKind.RadioboxMarked : PackIconKind.RadioboxBlank;
        icon.Opacity = active ? 1.0 : 0.45;
        if (Application.Current?.TryFindResource(active ? "MaterialDesign.Brush.Primary.Light" : "MaterialDesign.Brush.Foreground") is Brush brush)
        {
            icon.Foreground = brush;
        }
    }
}
