using System.Windows.Media;
using MaterialDesignColors;
using MaterialDesignColors.ColorManipulation;
using MaterialDesignThemes.Wpf;
using Microsoft.Win32;

namespace AoGPN.ViewModels;

public class ThemeSettingViewModel : MyReactiveObject
{
    private readonly PaletteHelper _paletteHelper = new();

    private IObservableCollection<Swatch> _swatches = new ObservableCollectionExtended<Swatch>();
    public IObservableCollection<Swatch> Swatches => _swatches;

    [Reactive]
    public Swatch SelectedSwatch { get; set; }

    [Reactive] public string CurrentTheme { get; set; }

    [Reactive] public int CurrentFontSize { get; set; }

    [Reactive] public string CurrentFontFamily { get; set; }

    [Reactive] public string CurrentLanguage { get; set; }

    public ThemeSettingViewModel()
    {
        _config = AppManager.Instance.Config;

        RegisterSystemColorSet(_config, ModifyTheme);

        BindingUI();
        RestoreUI();
    }

    private void RestoreUI()
    {
        ModifyTheme();
        ModifyFontSize();
        if (!_config.UiItem.ColorPrimaryName.IsNullOrEmpty())
        {
            var swatch = new SwatchesProvider().Swatches.FirstOrDefault(t => t.Name == _config.UiItem.ColorPrimaryName);
            if (swatch?.ExemplarHue?.Color is not null)
            {
                ChangePrimaryColor(swatch.ExemplarHue.Color);
            }
        }
    }

    private void BindingUI()
    {
        _swatches.AddRange(new SwatchesProvider().Swatches);
        if (!_config.UiItem.ColorPrimaryName.IsNullOrEmpty())
        {
            SelectedSwatch = _swatches.FirstOrDefault(t => t.Name == _config.UiItem.ColorPrimaryName);
        }
        CurrentTheme = _config.UiItem.CurrentTheme;
        CurrentFontSize = _config.UiItem.CurrentFontSize;
        CurrentFontFamily = _config.UiItem.CurrentFontFamily;
        CurrentLanguage = _config.UiItem.CurrentLanguage;

        this.WhenAnyValue(
                x => x.CurrentTheme,
                y => y != null && !y.IsNullOrEmpty())
            .Subscribe(c =>
             {
                 if (_config.UiItem.CurrentTheme != CurrentTheme)
                 {
                     _config.UiItem.CurrentTheme = CurrentTheme;
                     // Keep the persisted dashboard theme in lockstep: the startup
                     // push prefers DashboardTheme, so a native theme change must
                     // update it too or the WebView2 palette would stay stale.
                     _config.UiItem.DashboardTheme = MapThemeToWebViewId(CurrentTheme ?? string.Empty);
                     ModifyTheme();
                     _ = ConfigHandler.SaveConfig(_config);
                 }
             });

        this.WhenAnyValue(
          x => x.SelectedSwatch,
          y => y != null && !y.Name.IsNullOrEmpty())
             .Subscribe(c =>
             {
                 if (SelectedSwatch == null
                 || SelectedSwatch.Name.IsNullOrEmpty()
                 || SelectedSwatch.ExemplarHue == null
                 || SelectedSwatch.ExemplarHue?.Color == null)
                 {
                     return;
                 }
                 if (_config.UiItem.ColorPrimaryName != SelectedSwatch?.Name)
                 {
                     _config.UiItem.ColorPrimaryName = SelectedSwatch?.Name;
                     ChangePrimaryColor(SelectedSwatch.ExemplarHue.Color);
                     _ = ConfigHandler.SaveConfig(_config);
                 }
             });

        this.WhenAnyValue(
           x => x.CurrentFontSize,
           y => y > 0)
              .Subscribe(c =>
              {
                  if (_config.UiItem.CurrentFontSize != CurrentFontSize)
                  {
                      _config.UiItem.CurrentFontSize = CurrentFontSize;
                      ModifyFontSize();
                      _ = ConfigHandler.SaveConfig(_config);
                  }
              });

        this.WhenAnyValue(
          x => x.CurrentFontFamily,
          y => y != null && !y.IsNullOrEmpty())
             .Subscribe(c =>
             {
                 if (_config.UiItem.CurrentFontFamily != CurrentFontFamily)
                 {
                     _config.UiItem.CurrentFontFamily = CurrentFontFamily;
                     _ = ConfigHandler.SaveConfig(_config);
                     // Applies immediately via AppEvents ↔ DynamicResource — no restart needed.
                     AppEvents.FontFamilyChanged.Publish(CurrentFontFamily);
                 }
             });

        this.WhenAnyValue(
         x => x.CurrentLanguage,
         y => y != null && !y.IsNullOrEmpty())
            .Subscribe(c =>
            {
                if (CurrentLanguage.IsNotEmpty() && _config.UiItem.CurrentLanguage != CurrentLanguage)
                {
                    _config.UiItem.CurrentLanguage = CurrentLanguage;
                    Thread.CurrentThread.CurrentUICulture = new(CurrentLanguage);
                    _ = ConfigHandler.SaveConfig(_config);
                    // The dashboard re-localises instantly; native WPF chrome
                    // picks the new culture up on the next launch.
                    AppEvents.LanguageChanged.Publish(CurrentLanguage);
                }
            });
    }

    /// <summary>
    /// Applies the selected WPF theme and publishes a ThemeChanged event so the
    /// WebView2 dashboard can synchronize its gaming theme palette.
    /// </summary>
    public void ModifyTheme()
    {
        var theme = _paletteHelper.GetTheme();

        // Derive the Material Design base theme from our extended theme set.
        var baseTheme = CurrentTheme switch
        {
            nameof(ETheme.Dark) or nameof(ETheme.Dusk) or nameof(ETheme.NightSky) or nameof(ETheme.Aquatic) or nameof(ETheme.Crimson) or nameof(ETheme.Velocity) or nameof(ETheme.Venom) or nameof(ETheme.Synthwave) or nameof(ETheme.Cyberpunk) => BaseTheme.Dark,
            nameof(ETheme.Light) or nameof(ETheme.Desert) => BaseTheme.Light,
            _ => BaseTheme.Inherit,
        };
        theme.SetBaseTheme(baseTheme);

        // Apply custom primary and secondary colours when a named palette is recognised,
        // otherwise keep the Material Design defaults (MaterialDesignColors swatches).
        if (Palettes.TryGetValue(CurrentTheme ?? string.Empty, out var pair))
        {
            theme.PrimaryLight = new ColorPair(pair.Primary.Lighten());
            theme.PrimaryMid = new ColorPair(pair.Primary);
            theme.PrimaryDark = new ColorPair(pair.Primary.Darken());
            theme.SecondaryLight = new ColorPair(pair.Secondary.Lighten());
            theme.SecondaryMid = new ColorPair(pair.Secondary);
            theme.SecondaryDark = new ColorPair(pair.Secondary.Darken());
        }

        _paletteHelper.SetTheme(theme);

        Application.Current.Resources["AppTextRenderingMode"] =
            IsDarkTheme(theme.Background) ? TextRenderingMode.Grayscale : TextRenderingMode.ClearType;

        WindowsUtils.SetDarkBorder(Application.Current.MainWindow, CurrentTheme);

        // Push the active theme id to the WebView2 dashboard.
        AppEvents.ThemeChanged.Publish(MapThemeToWebViewId(CurrentTheme ?? string.Empty));
    }

    private static string MapThemeToWebViewId(string wpfTheme)
    {
        return wpfTheme switch
        {
            nameof(ETheme.Dark)      => "nebula",
            nameof(ETheme.Dusk)      => "plasma",
            nameof(ETheme.NightSky)  => "cryo",
            nameof(ETheme.Aquatic)   => "matrix",
            nameof(ETheme.Desert)    => "inferno",
            nameof(ETheme.Light)     => "phantom",
            nameof(ETheme.Crimson)   => "crimson",
            nameof(ETheme.Velocity)  => "velocity",
            nameof(ETheme.Venom)     => "venom",
            nameof(ETheme.Synthwave) => "synthwave",
            nameof(ETheme.Cyberpunk) => "cyberpunk",
            nameof(ETheme.FollowSystem) => "nebula",
            _ => "nebula",
        };
    }

    private static bool IsDarkTheme(System.Windows.Media.Color? background)
    {
        if (background is null)
            return false;

        return 0.299 * background.Value.R + 0.587 * background.Value.G + 0.114 * background.Value.B < 128;
    }

    private record PaletteRecord(System.Windows.Media.Color Primary, System.Windows.Media.Color Secondary);

    private static readonly Dictionary<string, PaletteRecord> Palettes = new()
    {
        [nameof(ETheme.Aquatic)] = new(
            System.Windows.Media.Color.FromRgb(20, 184, 166),
            System.Windows.Media.Color.FromRgb(124, 92, 246)),

        [nameof(ETheme.Desert)] = new(
            System.Windows.Media.Color.FromRgb(217, 119, 6),
            System.Windows.Media.Color.FromRgb(15, 118, 110)),

        [nameof(ETheme.Dusk)] = new(
            System.Windows.Media.Color.FromRgb(168, 85, 247),
            System.Windows.Media.Color.FromRgb(249, 115, 22)),

        [nameof(ETheme.NightSky)] = new(
            System.Windows.Media.Color.FromRgb(56, 189, 248),
            System.Windows.Media.Color.FromRgb(129, 140, 248)),

        [nameof(ETheme.Dark)] = new(
            System.Windows.Media.Color.FromRgb(139, 92, 246),
            System.Windows.Media.Color.FromRgb(6, 182, 212)),

        [nameof(ETheme.Light)] = new(
            System.Windows.Media.Color.FromRgb(99, 102, 241),
            System.Windows.Media.Color.FromRgb(165, 180, 252)),

        [nameof(ETheme.Crimson)] = new(
            System.Windows.Media.Color.FromRgb(225, 29, 72),
            System.Windows.Media.Color.FromRgb(251, 113, 133)),

        [nameof(ETheme.Velocity)] = new(
            System.Windows.Media.Color.FromRgb(239, 68, 68),
            System.Windows.Media.Color.FromRgb(59, 130, 246)),

        [nameof(ETheme.Venom)] = new(
            System.Windows.Media.Color.FromRgb(101, 163, 13),
            System.Windows.Media.Color.FromRgb(5, 150, 105)),

        [nameof(ETheme.Synthwave)] = new(
            System.Windows.Media.Color.FromRgb(192, 38, 211),
            System.Windows.Media.Color.FromRgb(219, 39, 119)),

        [nameof(ETheme.Cyberpunk)] = new(
            System.Windows.Media.Color.FromRgb(234, 179, 8),
            System.Windows.Media.Color.FromRgb(225, 29, 72)),
    };

    private void ModifyFontSize()
    {
        double size = CurrentFontSize;
        if (size < Global.MinFontSize)
            return;

        Application.Current.Resources["StdFontSize"] = size;
        Application.Current.Resources["StdFontSize1"] = size + 1;
        Application.Current.Resources["StdFontSize-1"] = size - 1;
        Application.Current.Resources["StdFontSize2"] = size + 2;
        Application.Current.Resources["StdFontSize4"] = size + 4;
        Application.Current.Resources["StdFontSize9"] = size + 9;
        Application.Current.Resources["StdFontSize10"] = size + 10;
        Application.Current.Resources["StdFontSize12"] = size + 12;

        Application.Current.Resources["MenuItemHeight"] = size + 20;
        Application.Current.Resources["DataGridRowHeight"] = size * 2 + 24;
        Application.Current.Resources["NavButtonMinHeight"] = size * 2 + 18;
    }

    public void ChangePrimaryColor(System.Windows.Media.Color color)
    {
        var theme = _paletteHelper.GetTheme();

        theme.PrimaryLight = new ColorPair(color.Lighten());
        theme.PrimaryMid = new ColorPair(color);
        theme.PrimaryDark = new ColorPair(color.Darken());

        _paletteHelper.SetTheme(theme);
    }

    public static void RegisterSystemColorSet(Config config, Action updateFunc)
    {
        SystemEvents.UserPreferenceChanged += (s, e) =>
        {
            if ((e.Category == UserPreferenceCategory.Color || e.Category == UserPreferenceCategory.General)
                && config.UiItem.CurrentTheme == nameof(ETheme.FollowSystem))
            {
                updateFunc?.Invoke();
            }
        };
    }
}
