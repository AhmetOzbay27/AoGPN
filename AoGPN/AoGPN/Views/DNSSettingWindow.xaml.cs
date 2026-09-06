namespace AoGPN.Views;

public partial class DNSSettingWindow
{
    private static Config _config;

    public DNSSettingWindow()
    {
        InitializeComponent();

        _config = AppManager.Instance.Config;

        cmbDirectDNSStrategy.ItemsSource = Global.DomainStrategy;
        cmbRemoteDNSStrategy.ItemsSource = Global.DomainStrategy;
        cmbProxyDialDNSStrategy.ItemsSource = Global.DomainStrategy;
        cmbDirectDNS.ItemsSource = Global.DomainDirectDNSAddress;
        cmbRemoteDNS.ItemsSource = Global.DomainRemoteDNSAddress;
        cmbBootstrapDNS.ItemsSource = Global.DomainPureIPDNSAddress;
        cmbFakeIPRange.ItemsSource = Global.FakeIPRanges;
        cmbDirectExpectedIPs.ItemsSource = Global.ExpectedIPs;

        cmbdomainStrategy4FreedomCompatible.ItemsSource = Global.DomainStrategy;
        cmbdomainDNSAddressCompatible.ItemsSource = Global.DomainPureIPDNSAddress;

        this.WhenActivated(disposables =>
        {
            this.Bind(ViewModel, vm => vm.UseSystemHosts, v => v.togUseSystemHosts.IsChecked).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.AddCommonHosts, v => v.togAddCommonHosts.IsChecked).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.FakeIP, v => v.togFakeIP.IsChecked).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.FakeIPRange, v => v.cmbFakeIPRange.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.BlockBindingQuery, v => v.togBlockBindingQuery.IsChecked).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.DirectDNS, v => v.cmbDirectDNS.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.RemoteDNS, v => v.cmbRemoteDNS.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.BootstrapDNS, v => v.cmbBootstrapDNS.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.Strategy4Freedom, v => v.cmbDirectDNSStrategy.SelectedItem).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.Strategy4Proxy, v => v.cmbRemoteDNSStrategy.SelectedItem).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.Strategy4ProxyDial, v => v.cmbProxyDialDNSStrategy.SelectedItem).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.Hosts, v => v.txtHosts.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.DirectExpectedIPs, v => v.cmbDirectExpectedIPs.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.ParallelQuery, v => v.togParallelQuery.IsChecked).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.ServeStale, v => v.togServeStale.IsChecked).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.EnableHappyEyeballs, v => v.togEnableHappyEyeballs.IsChecked).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.SaveCmd, v => v.btnSave).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.RayCustomDNSEnableCompatible, v => v.togRayCustomDNSEnableCompatible.IsChecked).DisposeWith(disposables);

            this.Bind(ViewModel, vm => vm.UseSystemHostsCompatible, v => v.togUseSystemHostsCompatible.IsChecked).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.DomainStrategy4FreedomCompatible, v => v.cmbdomainStrategy4FreedomCompatible.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.DomainDNSAddressCompatible, v => v.cmbdomainDNSAddressCompatible.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.NormalDNSCompatible, v => v.txtnormalDNSCompatible.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.TunDNSCompatible, v => v.txttunDNSCompatible.Text).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.ImportDefConfig4V2rayCompatibleCmd, v => v.btnImportDefConfig4V2rayCompatible).DisposeWith(disposables);

            this.WhenAnyValue(x => x.ViewModel.IsSimpleDNSEnabled)
                .Select(b => b ? Visibility.Collapsed : Visibility.Visible)
                .BindTo(this, x => x.txtBasicDNSSettingsInvalid.Visibility)
                .DisposeWith(disposables);
            this.WhenAnyValue(x => x.ViewModel.IsSimpleDNSEnabled)
                .Select(b => b ? Visibility.Collapsed : Visibility.Visible)
                .BindTo(this, x => x.txtAdvancedDNSSettingsInvalid.Visibility)
                .DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.IsSimpleDNSEnabled, v => v.gridBasicDNSSettings.IsEnabled).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.IsSimpleDNSEnabled, v => v.gridAdvancedDNSSettings.IsEnabled).DisposeWith(disposables);
        });
        WindowsUtils.SetDarkBorder(this, AppManager.Instance.Config.UiItem.CurrentTheme);
    }

    private void linkDnsObjectDoc_Click(object sender, RoutedEventArgs e)
    {
        ProcUtils.ProcessStart("https://xtls.github.io/config/dns.html#dnsobject");
    }

}
