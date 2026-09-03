namespace AoGPN.Views;

public partial class ConnectionMonitorView
{
    public ConnectionMonitorView()
    {
        InitializeComponent();

        menuCopyAddress.Click += MenuCopyAddress_Click;

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.Connections, v => v.lstConnections.ItemsSource).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.TrafficItems, v => v.lstTraffic.ItemsSource).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.AppTrafficItems, v => v.lstAppTraffic.ItemsSource).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.CountryItems, v => v.lstCountries.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedItem, v => v.lstConnections.SelectedItem).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedTrafficItem, v => v.lstTraffic.SelectedItem).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedAppTrafficItem, v => v.lstAppTraffic.SelectedItem).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.Filter, v => v.txtFilter.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.TrafficFilter, v => v.txtTrafficFilter.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.TrafficTabIndex, v => v.tabTrafficView.SelectedIndex).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.ClearTrafficCmd, v => v.btnClearTraffic).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.HideListeners, v => v.chkHideListeners.IsChecked).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.AutoRefresh, v => v.togAutoRefresh.IsChecked).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.RefreshCmd, v => v.btnRefresh).DisposeWith(disposables);
        });
    }

    private void MenuCopyAddress_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedItem?.RemoteAddress is { Length: > 0 } address)
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
}
