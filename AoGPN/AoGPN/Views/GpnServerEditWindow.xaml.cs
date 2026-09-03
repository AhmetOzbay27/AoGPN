namespace AoGPN.Views;

public partial class GpnServerEditWindow
{
    public GpnServerEditWindow()
    {
        InitializeComponent();

        Loaded += Window_Loaded;

        this.WhenActivated(disposables =>
        {
            this.Bind(ViewModel, vm => vm.Name, v => v.txtName.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.EndpointHost, v => v.txtEndpointHost.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.EndpointPort, v => v.txtEndpointPort.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.ServerPublicKey, v => v.txtServerPublicKey.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.ClientPrivateKey, v => v.txtClientPrivateKey.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.ClientAddress, v => v.txtClientAddress.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.Mtu, v => v.txtMtu.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.Dns, v => v.txtDns.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.Keepalive, v => v.txtKeepalive.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.IsEnabled, v => v.chkEnabled.IsChecked).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.SaveCmd, v => v.btnSave).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.GenerateKeyCmd, v => v.btnGenerateKey).DisposeWith(disposables);
        });
        WindowsUtils.SetDarkBorder(this, AppManager.Instance.Config.UiItem.CurrentTheme);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        txtEndpointHost.Focus();
    }
}
