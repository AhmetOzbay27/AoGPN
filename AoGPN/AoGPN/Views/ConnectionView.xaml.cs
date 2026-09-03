using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MaterialDesignThemes.Wpf;

namespace AoGPN.Views;

public partial class ConnectionView
{
    private readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _sessionSeconds;

    public ConnectionView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        btnConnectRing.Click += BtnConnectRing_Click;
        _sessionTimer.Tick += (_, _) =>
        {
            _sessionSeconds++;
            txtSessionTime.Text = FormatUptime(_sessionSeconds);
        };
        // The telemetry strip only samples while this dashboard is on screen.
        Loaded += (_, _) => ViewModel?.Telemetry.SetActive(true);
        Unloaded += (_, _) =>
        {
            ViewModel?.Telemetry.SetActive(false);
            _sessionTimer.Stop();
        };
        menuCopyAddress.Click += MenuCopyAddress_Click;
        menuAddToManual.Click += MenuAddToManual_Click;

        this.WhenActivated(disposables =>
        {
            // Mode
            colAction.ItemsSource = ViewModel.ManualActions;
            this.Bind(ViewModel, vm => vm.Mode, v => v.cmbMode.SelectedIndex).DisposeWith(disposables);

            // Segmented mode selector mirrors the (hidden) combo box
            btnModeOff.Checked += (_, _) => cmbMode.SelectedIndex = SplitTunnelViewModel.ModeOff;
            btnModeVpn.Checked += (_, _) => cmbMode.SelectedIndex = SplitTunnelViewModel.ModeVpn;
            btnModeManual.Checked += (_, _) => cmbMode.SelectedIndex = SplitTunnelViewModel.ModeManual;
            // AoGPN mode pills drive the same mode (Global VPN = VPN, Game Tunnel = Manuel)
            btnPillVpn.Checked += (_, _) => cmbMode.SelectedIndex = SplitTunnelViewModel.ModeVpn;
            btnPillGpn.Checked += (_, _) => cmbMode.SelectedIndex = SplitTunnelViewModel.ModeManual;
            this.WhenAnyValue(v => v.cmbMode.SelectedIndex)
                .Subscribe(i =>
                {
                    btnModeOff.IsChecked = i == SplitTunnelViewModel.ModeOff;
                    btnModeVpn.IsChecked = i == SplitTunnelViewModel.ModeVpn;
                    btnModeManual.IsChecked = i == SplitTunnelViewModel.ModeManual;
                    btnPillVpn.IsChecked = i == SplitTunnelViewModel.ModeVpn;
                    btnPillGpn.IsChecked = i == SplitTunnelViewModel.ModeManual;
                })
                .DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.IsManual, v => v.pnlManualHint.Visibility, conversionHint: BooleanToVisibilityHint.UseHidden, vmToViewConverterOverride: new BooleanToVisibilityTypeConverter()).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.IsVpn, v => v.pnlVpnHint.Visibility, conversionHint: BooleanToVisibilityHint.UseHidden, vmToViewConverterOverride: new BooleanToVisibilityTypeConverter()).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.NeedAdmin, v => v.pnlNeedAdmin.Visibility, conversionHint: BooleanToVisibilityHint.UseHidden, vmToViewConverterOverride: new BooleanToVisibilityTypeConverter()).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.AnyNeedsTun, v => v.pnlTunWarning.Visibility, conversionHint: BooleanToVisibilityHint.UseHidden, vmToViewConverterOverride: new BooleanToVisibilityTypeConverter()).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.StatusText, v => v.txtStatus.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.ActiveRoutingRemarks, v => v.txtActiveRoutingRemarks.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.DomainInput, v => v.txtDomain.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.AutoConnectOnGameStart, v => v.chkAutoGameConnect.IsChecked).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.TriggerPending, v => v.pnlTriggerPending.Visibility, conversionHint: BooleanToVisibilityHint.UseHidden, vmToViewConverterOverride: new BooleanToVisibilityTypeConverter()).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.TriggerPendingText, v => v.txtTriggerPending.Text).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.CancelTriggerCmd, v => v.btnCancelTrigger).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.RefreshCmd, v => v.btnRefreshApps).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddAppCmd, v => v.btnAddApp).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.AddDomainCmd, v => v.btnAddDomain).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.EditCmd, v => v.btnEditApp).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ImportClipboardCmd, v => v.btnImportClipboard).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RemoveAppCmd, v => v.btnRemoveApp).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ApplyCmd, v => v.btnApply).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RebootAsAdminCmd, v => v.btnRebootAdmin).DisposeWith(disposables);

            // Manual connection list (filtered view + live search)
            this.OneWayBind(ViewModel, vm => vm.FilteredApps, v => v.lstApps.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.ManualFilter, v => v.txtManualFilter.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedApp, v => v.lstApps.SelectedItem).DisposeWith(disposables);

            // Live telemetry dashboard (ping / loss / speed sparklines)
            ViewModel.Telemetry.SamplesChanged += OnTelemetrySamplesChanged;
            System.Reactive.Disposables.Disposable.Create(() => ViewModel.Telemetry.SamplesChanged -= OnTelemetrySamplesChanged).DisposeWith(disposables);
            OnTelemetrySamplesChanged();

            // Connection center: ring visuals + session uptime follow the live state
            this.WhenAnyValue(v => v.ViewModel.Telemetry.IsConnected)
                .Subscribe(OnConnectionStateChanged)
                .DisposeWith(disposables);

            // Node info cards mirror the real running server
            this.OneWayBind(StatusBarViewModel.Instance, vm => vm.RunningServerDisplay, v => v.txtNodeName.Text).DisposeWith(disposables);
            this.OneWayBind(StatusBarViewModel.Instance, vm => vm.RunningInfoDisplay, v => v.txtNodeSub.Text).DisposeWith(disposables);

            // Live monitoring (merged)
            this.OneWayBind(ViewModel, vm => vm.Monitor.Connections, v => v.lstConnections.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.Monitor.SelectedItem, v => v.lstConnections.SelectedItem).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.Monitor.Filter, v => v.txtFilter.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.Monitor.HideListeners, v => v.chkHideListeners.IsChecked).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.Monitor.AutoRefresh, v => v.togAutoRefresh.IsChecked).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.Monitor.RefreshCmd, v => v.btnRefreshMonitor).DisposeWith(disposables);

            ViewModel.BrowseExeInteraction.RegisterHandler(interaction =>
            {
                if (UI.OpenFileDialog(out var fileName, "Applications|*.exe|All files|*.*") != true)
                {
                    interaction.SetOutput(null);
                    return;
                }
                interaction.SetOutput(fileName);
            }).DisposeWith(disposables);

            ViewModel.GetClipboardTextInteraction.RegisterHandler(interaction =>
            {
                string? text = null;
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        text = Clipboard.GetText();
                    }
                }
                catch
                {
                }
                interaction.SetOutput(text);
            }).DisposeWith(disposables);
        });
    }

    private void OnTelemetrySamplesChanged()
    {
        var telemetry = ViewModel?.Telemetry;
        if (telemetry is null)
        {
            return;
        }
        sparkPing.Values = telemetry.GetPingSamples();
        sparkDown.Values = telemetry.GetDownSamples();
        sparkUp.Values = telemetry.GetUpSamples();
    }

    private void OnConnectionStateChanged(bool connected)
    {
        var cyan = (Brush)FindResource("GpnCyan");
        var emerald = (Brush)FindResource("GpnGreen");

        if (btnConnectRing.Template?.FindName("CoreText", btnConnectRing) is TextBlock coreText)
        {
            coreText.Text = connected ? "CONNECTED" : "CONNECT";
        }
        if (btnConnectRing.Template?.FindName("CoreSub", btnConnectRing) is TextBlock coreSub)
        {
            coreSub.Text = connected ? "GLOBAL VPN · AKTİF" : "GLOBAL VPN";
        }
        if (btnConnectRing.Template?.FindName("CoreIcon", btnConnectRing) is PackIcon coreIcon)
        {
            coreIcon.Foreground = connected ? emerald : cyan;
        }
        // The ring core itself also switches to the emerald glow when connected,
        // exactly like the design's connected state.
        if (btnConnectRing.Template?.FindName("Core", btnConnectRing) is Border core)
        {
            core.BorderBrush = connected
                ? new SolidColorBrush(Color.FromArgb(0x55, 0x34, 0xD3, 0x99))
                : new SolidColorBrush(Color.FromArgb(0x55, 0x7C, 0x4D, 0xFF));
            core.Effect = new DropShadowEffect
            {
                BlurRadius = 22,
                ShadowDepth = 0,
                Opacity = 0.5,
                Color = connected ? Color.FromRgb(0x34, 0xD3, 0x99) : Color.FromRgb(0x06, 0xB6, 0xD4),
            };
        }

        txtProtoName.Text = connected ? "Hybrid Tunnel" : "Global VPN Tunnel";

        _sessionTimer.Stop();
        if (connected)
        {
            _sessionSeconds = 0;
            txtSessionTime.Text = "00:00:00";
            _sessionTimer.Start();
        }
    }

    private void BtnConnectRing_Click(object sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null)
        {
            return;
        }
        // The ring is the big connect/disconnect control (Global VPN).
        vm.Mode = vm.Telemetry.IsConnected ? SplitTunnelViewModel.ModeOff : SplitTunnelViewModel.ModeVpn;
    }

    private static string FormatUptime(int seconds)
    {
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        var s = seconds % 60;
        return $"{h:D2}:{m:D2}:{s:D2}";
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var vm = ViewModel;
        if (vm != null)
        {
            vm.RefreshCmd.Execute().Subscribe(_ => { }, ex => Logging.SaveLog("ConnectionView.OnLoaded", ex));
        }
    }

    private void MenuCopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.Monitor.SelectedItem?.RemoteAddress is { Length: > 0 } address)
        {
            try
            {
                Clipboard.SetDataObject(address, true);
            }
            catch
            {
            }
        }
    }

    private void MenuAddToManual_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.AddRemoteToManualList(ViewModel.Monitor.SelectedItem);
    }

    private static bool IsValidDrop(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop)
            && data.GetData(DataFormats.FileDrop) is IEnumerable<string> files)
        {
            return files.Any(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        }
        return data.GetDataPresent(DataFormats.UnicodeText);
    }

    private void ManualList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = IsValidDrop(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        pnlDropHint.Visibility = e.Effects == DragDropEffects.Copy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ManualList_DragLeave(object sender, DragEventArgs e)
    {
        var pos = e.GetPosition(pnlManualList);
        if (pos.X < 0 || pos.Y < 0 || pos.X > pnlManualList.ActualWidth || pos.Y > pnlManualList.ActualHeight)
        {
            pnlDropHint.Visibility = Visibility.Collapsed;
        }
    }

    private async void ManualList_Drop(object sender, DragEventArgs e)
    {
        pnlDropHint.Visibility = Visibility.Collapsed;
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)
                && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                var exes = files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (exes.Length > 0)
                {
                    await ViewModel.AddDroppedFilesAsync(exes);
                }
            }
            else if (e.Data.GetDataPresent(DataFormats.UnicodeText)
                && e.Data.GetData(DataFormats.UnicodeText) is string text
                && text.IsNotEmpty())
            {
                await ViewModel.AddDroppedTextAsync(text);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ConnectionView.ManualList_Drop", ex);
        }
    }
}
