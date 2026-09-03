using System.Windows.Media;
using AoGPN.ViewModels;

namespace AoGPN.Views;

/// <summary>
/// ThemeSettingView.xaml
/// </summary>
public partial class ThemeSettingView
{
    public ThemeSettingView()
    {
        InitializeComponent();
        ViewModel = new ThemeSettingViewModel();

        cmbCurrentTheme.ItemsSource = Utils.GetEnumNames<ETheme>();
        cmbCurrentFontSize.ItemsSource = Enumerable.Range(Global.MinFontSize, Global.MinFontSizeCount).ToList();
        cmbCurrentFontFamily.ItemsSource = GetSystemFontFamilies();
        cmbCurrentLanguage.ItemsSource = Global.Languages;

        this.WhenActivated(disposables =>
        {
            this.Bind(ViewModel, vm => vm.CurrentTheme, v => v.cmbCurrentTheme.SelectedValue).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.Swatches, v => v.cmbSwatches.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedSwatch, v => v.cmbSwatches.SelectedItem).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.CurrentFontSize, v => v.cmbCurrentFontSize.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.CurrentFontFamily, v => v.cmbCurrentFontFamily.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.CurrentLanguage, v => v.cmbCurrentLanguage.Text).DisposeWith(disposables);
        });
    }

    /// <summary>
    /// List of installed system fonts, with an empty first option meaning "default".
    /// </summary>
    private static List<string> GetSystemFontFamilies()
    {
        return Fonts.SystemFontFamilies
            .Select(t => t.Source)
            .Distinct()
            .OrderBy(t => t)
            .Prepend("")
            .ToList();
    }
}
