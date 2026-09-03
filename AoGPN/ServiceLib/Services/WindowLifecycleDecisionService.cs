using ServiceLib.Models.Configs;

namespace ServiceLib.Services;

public class WindowLifecycleDecisionService
{
    public bool ShouldHideOnMinimize(Config config)
        => config?.UiItem?.Minimize2Tray == true;

    public bool ShouldHideOnClose(Config config)
        => config?.UiItem?.Hide2TrayWhenClose == true;

    public bool ShouldShowOnToggle(bool isInTaskbar, bool isMinimized)
        => TrayWindowCoordinator.ShouldShowOnToggle(isInTaskbar, isMinimized);

    public WindowLifecycleAction DecideClose(Config config, bool allowClose)
    {
        if (allowClose) return WindowLifecycleAction.AllowClose;
        return ShouldHideOnClose(config) ? WindowLifecycleAction.HideToTray : WindowLifecycleAction.Exit;
    }

    public WindowLifecycleAction DecideStateChange(Config config, bool isMinimized, bool startupPending)
    {
        if (!isMinimized || startupPending || !ShouldHideOnMinimize(config)) return WindowLifecycleAction.None;
        return WindowLifecycleAction.HideToTray;
    }
}

public enum WindowLifecycleAction
{
    None,
    AllowClose,
    HideToTray,
    Exit,
}
