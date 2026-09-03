using System.ComponentModel;

namespace AoGPN.Base;

public class WindowBase<TViewModel> : ReactiveWindow<TViewModel> where TViewModel : class
{
    public WindowBase()
    {
        Loaded += OnLoaded;
    }

    protected virtual void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            var sizeItem = ConfigHandler.GetWindowSizeItem(AppManager.Instance.Config, GetType().Name);
            if (sizeItem == null)
            {
                return;
            }

            Width = Math.Min(sizeItem.Width, SystemParameters.WorkArea.Width);
            Height = Math.Min(sizeItem.Height, SystemParameters.WorkArea.Height);

            // Restore the saved position when it is still reachable on some
            // monitor; otherwise (monitor unplugged, resolution changed) fall
            // back to centering on the primary work area. Configs written before
            // positions existed carry no Left/Top and also fall back to centering.
            var left = sizeItem.Left ?? 0;
            var top = sizeItem.Top ?? 0;
            var minVisibleWidth = Math.Min(Width, 200);
            var minVisibleHeight = Math.Min(Height, 120);
            var hasPosition = sizeItem.Left is not null && sizeItem.Top is not null;
            var offScreen = left + minVisibleWidth < SystemParameters.VirtualScreenLeft ||
                            left > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - minVisibleWidth ||
                            top + minVisibleHeight < SystemParameters.VirtualScreenTop ||
                            top > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - minVisibleHeight;
            if (!hasPosition || offScreen)
            {
                left = (int)(SystemParameters.WorkArea.Left + ((SystemParameters.WorkArea.Width - Width) / 2));
                top = (int)(SystemParameters.WorkArea.Top + ((SystemParameters.WorkArea.Height - Height) / 2));
            }

            Left = left;
            Top = top;

            if (sizeItem.IsMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
        catch { }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        try
        {
            var isMaximized = WindowState == WindowState.Maximized;
            ConfigHandler.SaveWindowSizeItem(AppManager.Instance.Config, GetType().Name,
                isMaximized ? RestoreBounds.Width : Width,
                isMaximized ? RestoreBounds.Height : Height,
                isMaximized,
                isMaximized ? RestoreBounds.Left : Left,
                isMaximized ? RestoreBounds.Top : Top);
        }
        catch { }
    }
}
