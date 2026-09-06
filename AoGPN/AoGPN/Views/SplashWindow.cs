using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ServiceLib.Common;

namespace AoGPN.Views;

/// <summary>
/// The boot splash: only the floating logo, a real stage-driven loading bar, the
/// status line and the version — with NO window background (the desktop shows
/// through everywhere except the logo, bar and text).
///
/// It deliberately does NOT use a layered window (<c>WS_EX_LAYERED</c> /
/// <c>AllowsTransparency</c> / <c>UpdateLayeredWindow</c>): layered windows render
/// as a solid black rectangle on machines where the driver or session cannot
/// composite them (software rendering, RDP, some VMs) — exactly the "black box
/// behind the logo" this rewrite removes. Instead the splash is a normal, opaque
/// WPF window whose shape is cut to the content silhouette with
/// <see cref="SetWindowRgn"/>: WPF draws the logo/bar/text as usual (hardware OR
/// software), and the region makes every other pixel fall outside the window, so
/// the desktop shows through. No layering → no black box, in any environment.
///
/// The window region is cut from a rendered snapshot of the content: the shield
/// silhouette + the full bar track + the status/version glyphs. Cutting text to
/// its glyphs (instead of a solid band over the row) is what keeps the desktop
/// visible BETWEEN the letters — a solid band would fill that gap with the
/// window's opaque background and read as a "black bar behind the text". The
/// status line is deliberately a FIXED string for the splash's whole (short)
/// life: re-cutting the region to a new string mid-flight shows one composited
/// frame of the old letters clipped by the new shape (the visible "bozulma" on
/// stage changes), so the shape is built ONCE and never changes.
///
/// Shown by <see cref="App.OnStartup"/> before any heavy initialization, and
/// dismissed by <see cref="MainWindow.RevealStartupWindow"/> at the exact moment
/// the WebView2 dashboard has loaded and been seeded.
/// </summary>
public sealed class SplashWindow : IDisposable
{
    // Extended window styles applied after the window exists.
    private const int WS_EX_TOOLWINDOW = 0x00000080;   // no Alt-Tab entry
    private const int WS_EX_NOACTIVATE = 0x08000000;   // never steals focus
    private const int WS_EX_TRANSPARENT = 0x00000020;  // clicks fall through
    private const int GWL_EXSTYLE = -20;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int RGN_OR = 2;

    // Layout geometry (DIPs). The logo canvas is 256x256 (the app icon); the
    // shield already carries ~8% padding inside it, so showing it at 240 DIP
    // leaves a clean margin around the emblem.
    private const double LogoWidth = 240;
    private const double BarTrackWidth = 190;
    private const double BarHeight = 5;
    private const double BarCornerRadius = 2.5;

    // If nothing dismisses the splash (startup crashed before any reveal path),
    // it must still never linger on screen. Deliberately longer than the
    // MainWindow 15 s reveal safety net so the splash always outlives it and
    // there is never a dead gap where neither is visible.
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromSeconds(16);

    private Window? _window;
    private IntPtr _hwnd = IntPtr.Zero;
    private double _dpi = 96.0;
    private int _pxWidth;
    private int _pxHeight;

    private readonly Grid _root;
    private readonly TextBlock _statusText;
    private readonly TextBlock _versionText;
    private readonly Border _barFill;
    private readonly DispatcherTimer _maxLifetimeTimer;
    private bool _closeStarted;
    private bool _disposed;

    public SplashWindow()
    {
        _root = BuildTree(out _statusText, out _versionText, out _barFill);

        // Lay the tree out so the exact size is known for the window / region.
        _root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _root.Arrange(new Rect(0, 0, _root.DesiredSize.Width, _root.DesiredSize.Height));
        _root.UpdateLayout();

        _maxLifetimeTimer = new DispatcherTimer { Interval = MaxLifetime };
        _maxLifetimeTimer.Tick += (_, _) => BeginCloseAsync();
    }

    private static Grid BuildTree(out TextBlock statusText, out TextBlock versionText, out Border barFill)
    {
        // The floating logo. Splash.png is embedded as a resource and carries a
        // true alpha channel (corners fully transparent), so only the emblem shows.
        var logo = new BitmapImage();
        var uri = new Uri("pack://application:,,,/AoGPN;component/Resources/Splash.png");
        using (var stream = Application.GetResourceStream(uri)?.Stream)
        {
            if (stream != null)
            {
                logo.BeginInit();
                logo.CacheOption = BitmapCacheOption.OnLoad;
                logo.StreamSource = stream;
                logo.EndInit();
                logo.Freeze();
            }
        }
        var image = new Image
        {
            Source = logo,
            Width = LogoWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            Stretch = Stretch.Uniform,
        };
        // Best-quality downscale of the (high-res) source so the logo never
        // looks soft or pixelated at high-DPI physical sizes.
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        // Loading bar track (rounded, dark) with a gliding fill that reflects
        // real startup stages (see SetProgress).
        var track = new Border
        {
            Width = BarTrackWidth,
            Height = BarHeight,
            CornerRadius = new CornerRadius(BarCornerRadius),
            Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x1A, 0x24, 0x34)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 16, 0, 0),
        };

        statusText = new TextBlock
        {
            // The status line is FIXED for the splash's whole life (the class
            // comment explains why: re-cutting the window shape for a changing
            // string glitches one composited frame). The bar still reflects
            // live progress, so a static "interface loading" line is enough.
            Text = "Arayüz yükleniyor…",
            // Pinned to the platform font: the splash text must render and
            // measure identically before/after the app font resource is
            // applied (the region is cut from a snapshot of this tree, so any
            // later font/layout change could push glyphs outside the window
            // region and clip them). Segoe UI carries the full Turkish + "…"
            // glyph set.
            FontFamily = new FontFamily("Segoe UI"),
            // 14 pt per the user's request (smaller than the old 20 pt) and set
            // to the logo's cyan (#22D3EE — same tone as the bar-fill gradient
            // and the shield's left edge) instead of white, so it reads as part
            // of the brand on both dark and light wallpapers.
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
        };
        // GrayScale (not ClearType): the window shape is cut from a bitmap
        // snapshot of this tree, and WPF can only rasterize text over a
        // TRANSPARENT background in grayscale. Pinning the display to the same
        // mode makes the on-screen glyph coverage identical to the snapshot's,
        // so the region hugs the letters exactly — ClearType would smear the
        // glyphs a fraction of a pixel past the cut shape and clip their edges.
        TextOptions.SetTextRenderingMode(statusText, TextRenderingMode.Grayscale);
        barFill = new Border
        {
            Width = 0,
            Height = BarHeight,
            CornerRadius = new CornerRadius(BarCornerRadius),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new LinearGradientBrush(
                Color.FromRgb(0x22, 0xD3, 0xEE),
                Color.FromRgb(0xE8, 0x79, 0xF9),
                0),
        };
        track.Child = barFill;

        var version = new TextBlock
        {
            Text = $"V{Utils.GetVersionInfo()}",
            FontFamily = new FontFamily("Segoe UI"), // see statusText comment
            FontSize = 13,
            // Bright enough to read against the desktop: the old #8A94A6 at 11 px
            // had too little contrast on dark wallpapers and read as broken/faded.
            Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xC2, 0xD2)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            // See the statusText comment: grayscale AA keeps the glyph coverage
            // identical between the region snapshot and the on-screen text.
            Margin = new Thickness(0, 10, 0, 0),
        };
        TextOptions.SetTextRenderingMode(version, TextRenderingMode.Grayscale);
        versionText = version;

        // Order per the user's request: emblem on top, then the loading bar,
        // with the status line BELOW the bar (and the version under that).
        var panel = new StackPanel();
        panel.Children.Add(image);
        panel.Children.Add(track);
        panel.Children.Add(statusText);
        panel.Children.Add(version);

        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(panel);
        return root;
    }

    /// <summary>Shows the splash centered, topmost and click-through.</summary>
    public void Show()
    {
        if (_window != null)
        {
            return;
        }

        // A normal, OPAQUE WPF window (no AllowsTransparency). It is shaped to
        // the content silhouette via SetWindowRgn, so the transparent margins
        // fall outside the window and the desktop shows through. Because it is
        // not a layered window it renders correctly under hardware AND software
        // rendering — no black box.
        var window = new Window
        {
            Title = "AO GPN",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = false,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Background = Brushes.Black, // never visible: every pixel outside the region is cut
            Width = _root.DesiredSize.Width,
            Height = _root.DesiredSize.Height,
        };
        window.Content = _root;
        window.SourceInitialized += OnSourceInitialized;
        window.Closed += (_, _) => Dispose();
        // Assign BEFORE Show(): Show() raises SourceInitialized synchronously,
        // and OnSourceInitialized must see the window. Previously the field was
        // set after Show(), so the handler crashed with ArgumentNullException
        // and the whole startup aborted at the splash's first frame.
        _window = window;
        window.Show();

        _maxLifetimeTimer.Start();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(_window!).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        _hwnd = handle;

        // Click-through + never activate + no Alt-Tab (a boot splash must not
        // steal focus or block clicks).
        var exStyle = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE,
            exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);

        // DPI-aware physical sizing + centering.
        _dpi = GetDpiForWindow(handle);
        var scale = _dpi / 96.0;
        _pxWidth = Math.Max(1, (int)Math.Round(_root.DesiredSize.Width * scale));
        _pxHeight = Math.Max(1, (int)Math.Round(_root.DesiredSize.Height * scale));

        var screenW = GetSystemMetrics(SM_CXSCREEN);
        var screenH = GetSystemMetrics(SM_CYSCREEN);
        var left = (screenW - _pxWidth) / 2;
        var top = (screenH - _pxHeight) / 2;
        DiagLog.Write($"WEBVIEW_BOOT splash-dpi dpi={_dpi} scale={scale:N2} px={_pxWidth}x{_pxHeight} @{left},{top}");

        SetWindowPos(handle, HWND_TOPMOST, left, top, _pxWidth, _pxHeight,
            SWP_SHOWWINDOW | SWP_NOACTIVATE);

        // Shape the window once to the content silhouette (shield + bar + text).
        // The status line never changes afterwards (see the class comment), so
        // the shape stays valid for the whole splash lifetime — no re-cut, no
        // transition frame where old content shows clipped by a new shape.
        SetSplashRegion();
    }

    private void SetSplashRegion()
    {
        if (_hwnd == IntPtr.Zero || _pxWidth <= 0 || _pxHeight <= 0)
        {
            return;
        }

        // Ensure the (fixed) status string is laid out before it is snapshotted.
        _root.UpdateLayout();

        // Snapshot the content with the bar at FULL width: the region then
        // covers the whole bar track, so the gliding fill animation stays inside
        // the shape at every progress step without a rebuild.
        var originalFill = _barFill.Width;
        _barFill.Width = BarTrackWidth;
        _root.UpdateLayout();

        try
        {
            var bitmap = new RenderTargetBitmap(_pxWidth, _pxHeight, _dpi, _dpi, PixelFormats.Pbgra32);
            bitmap.Render(_root);
            var stride = _pxWidth * 4;
            var pixels = new byte[stride * _pxHeight];
            bitmap.CopyPixels(pixels, stride, 0);

            // Low threshold (32) on purpose: the content includes small text,
            // whose anti-aliased glyph pixels mostly carry alpha well below the
            // old 128 cutoff — a high threshold cut the thin strokes, so the
            // status/version lines rendered eroded ("bozuk"). Empty background
            // is alpha 0, so 32 keeps every glyph pixel while still excluding it.
            var region = BuildRegionFromAlpha(pixels, stride, _pxWidth, _pxHeight, 32);
            if (region != IntPtr.Zero)
            {
                // NO solid bands over the text rows: the snapshot's alpha
                // silhouette already contains the status/version glyphs. A solid
                // row band would give the window's opaque background to the
                // empty pixels around the letters — the "black behind the text"
                // block this glyph-shaped cut exists to remove.
                var shaped = SetWindowRgn(_hwnd, region, true);
                DiagLog.Write($"WEBVIEW_BOOT splash-region applied={shaped} px={_pxWidth}x{_pxHeight}");
            }
        }
        finally
        {
            _barFill.Width = originalFill;
            _root.UpdateLayout();
        }
    }

    /// <summary>
    /// Builds a window region covering every pixel whose alpha exceeds the
    /// threshold, as a union of per-row runs (standard shaped-window technique).
    /// The caller must hand the returned region to SetWindowRgn (which takes
    /// ownership); it is otherwise deleted.
    /// </summary>
    private static IntPtr BuildRegionFromAlpha(byte[] bgra, int stride, int width, int height, byte threshold)
    {
        var master = CreateRectRgn(0, 0, 0, 0);
        for (var y = 0; y < height; y++)
        {
            var row = y * stride;
            var x = 0;
            while (x < width)
            {
                while (x < width && bgra[row + x * 4 + 3] <= threshold)
                {
                    x++;
                }

                if (x >= width)
                {
                    break;
                }

                var start = x;
                while (x < width && bgra[row + x * 4 + 3] > threshold)
                {
                    x++;
                }

                var run = CreateRectRgn(start, y, x, y + 1);
                if (run != IntPtr.Zero)
                {
                    CombineRgn(master, master, run, RGN_OR);
                    DeleteObject(run);
                }
            }
        }

        return master;
    }

    /// <summary>
    /// Advances the loading bar to a real startup stage (0-100). The fill glides
    /// smoothly to the target so the bar never jumps or looks frozen, and each
    /// call animates from the current position. Ignored once the close has started.
    /// </summary>
    public void SetProgress(double percent)
    {
        if (_closeStarted)
        {
            return;
        }

        var target = Math.Clamp(percent, 0, 100) / 100 * BarTrackWidth;
        _barFill.BeginAnimation(
            FrameworkElement.WidthProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
    }

    /// <summary>Closes the splash. Idempotent.</summary>
    public void BeginCloseAsync()
    {
        if (_closeStarted)
        {
            return;
        }

        _closeStarted = true;
        _maxLifetimeTimer.Stop();
        Dispose();
    }

    /// <summary>Closes instantly for error/exit paths.</summary>
    public void CloseNow()
    {
        _maxLifetimeTimer.Stop();
        if (_closeStarted)
        {
            return;
        }

        _closeStarted = true;
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _maxLifetimeTimer.Stop();
        if (_window != null)
        {
            _window.Close();
            _window = null;
        }

        _hwnd = IntPtr.Zero;
    }

    #region Win32 interop

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr hDestRgn, IntPtr hSrcRgn1, IntPtr hSrcRgn2, int fnCombineMode);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    #endregion
}