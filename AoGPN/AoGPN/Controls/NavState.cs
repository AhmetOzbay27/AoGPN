using System.Windows;

namespace AoGPN.Controls;

/// <summary>
/// Marks the active sidebar nav button so its template can render the AoGPN accent
/// bar (violet→cyan gradient) and active background, mirroring the design mockup.
/// </summary>
public static class NavState
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive",
        typeof(bool),
        typeof(NavState),
        new PropertyMetadata(false));

    public static bool GetIsActive(DependencyObject obj) => (bool)obj.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject obj, bool value) => obj.SetValue(IsActiveProperty, value);
}
