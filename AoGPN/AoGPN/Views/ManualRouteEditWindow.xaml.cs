namespace AoGPN.Views;

public partial class ManualRouteEditWindow
{
    public ManualRouteEditWindow()
    {
        InitializeComponent();

        Loaded += Window_Loaded;

        this.WhenActivated(disposables =>
        {
            cmbAction.ItemsSource = ViewModel?.ManualActions;

            this.OneWayBind(ViewModel, vm => vm.EntryTypeLabel, v => v.txtEntryType.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.DisplayName, v => v.txtDisplayName.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.Value, v => v.txtValue.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.Action, v => v.cmbAction.SelectedValue).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.SaveCmd, v => v.btnSave).DisposeWith(disposables);
        });
        WindowsUtils.SetDarkBorder(this, AppManager.Instance.Config.UiItem.CurrentTheme);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        txtValue.Focus();
        txtValue.SelectAll();
    }
}
