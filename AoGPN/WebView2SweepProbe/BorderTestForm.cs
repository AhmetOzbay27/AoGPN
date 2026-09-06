using System.Runtime.InteropServices;

namespace WebView2SweepProbe;

/// <summary>
/// Border diagnostic for the AoGPN main window's purple frame on Windows 11.
///
/// Reproduces the app's exact native window setup (borderless + WS_THICKFRAME
/// restored, immersive dark mode) and then measures what DWM actually draws at
/// the window edge, BEFORE and AFTER setting DWMWA_BORDER_COLOR — first to a
/// loud red (proves whether the attribute controls the border at all on this
/// machine) and then to the app's own dark background (#0B0F19).
///
/// Usage: WebView2SweepProbe --bordertest
/// </summary>
internal sealed class BorderTestForm : Form
{
    private const int GwlStyle = -16;
    private const int WsThickFrame = 0x00040000;
    private const int WsMaximizeBox = 0x00010000;

    private static readonly Color WindowBg = Color.FromArgb(0x0B, 0x0F, 0x19);
    private static readonly int RedColorRef = 0x000000FF;   // COLORREF: pure red
    private static readonly int DarkColorRef = 0x00190F0B; // COLORREF #0B0F19

    public BorderTestForm()
    {
        Text = "AoGPN border probe";
        BackColor = WindowBg;
        ClientSize = new Size(480, 320);
        StartPosition = FormStartPosition.Manual;
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Left + 60, wa.Top + 60);
        TopMost = true;
        FormBorderStyle = FormBorderStyle.None;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Mirror MainWindow.OnSourceInitialized: restore the thick frame onto
        // the borderless window.
        var hwnd = Handle;
        var style = GetWindowLong(hwnd, GwlStyle);
        style |= WsThickFrame | WsMaximizeBox;
        SetWindowLong(hwnd, GwlStyle, style);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);

        // Immersive dark mode (the app does this too).
        var dark = 1;
        var hr20 = DwmSetWindowAttribute(hwnd, 20, ref dark, 4);

        Console.WriteLine($"OS            : {Environment.OSVersion.VersionString} (build {Environment.OSVersion.Version.Build})");
        Console.WriteLine($"Dwm attr 20   : hr=0x{hr20:X8}");
        Console.WriteLine();

        CaptureAndScan("BASELINE (no border color set)");

        var hrRed = SetBorderColor(hwnd, RedColorRef);
        Console.WriteLine($"Dwm attr 34   : hr=0x{hrRed:X8} (red)");
        CaptureAndScan("AFTER BORDER_COLOR=RED");

        SetBorderColor(hwnd, DarkColorRef);
        CaptureAndScan("AFTER BORDER_COLOR=#0B0F19");

        Console.WriteLine("=== END BORDER TEST ===");
        Close();
    }

    private int SetBorderColor(IntPtr hwnd, int colorRef)
    {
        var value = colorRef;
        return DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref value, 4);
    }

    /// <summary>
    /// Copies the window rect from the screen and scans the outermost pixels:
    /// for each of the four edges, the middle 50% of the edge is sampled at
    /// offsets 1..8 px inward, and the dominant color at each offset is
    /// reported. A border drawn by DWM shows up as a distinct ring at offset 1.
    /// </summary>
    private void CaptureAndScan(string label)
    {
        Application.DoEvents();
        Thread.Sleep(900); // let DWM repaint the frame

        GetWindowRect(Handle, out var rect);
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        using var bmp = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, bmp.Size);
        }

        Console.WriteLine($"--- {label} ---");
        foreach (var edge in new[] { "top", "bottom", "left", "right" })
        {
            var colors = new Dictionary<int, (int count, Color color)>();
            var start = width / 4;
            var end = width - width / 4;
            for (var offset = 1; offset <= 8; offset++)
            {
                var counts = new Dictionary<int, int>();
                var sums = new Dictionary<int, long[]>();
                for (var i = start; i < end; i++)
                {
                    int x, y;
                    switch (edge)
                    {
                        case "top": x = i; y = offset; break;
                        case "bottom": x = i; y = height - 1 - offset; break;
                        case "left": x = offset; y = i + height / 4; break;
                        default: x = width - 1 - offset; y = i + height / 4; break;
                    }

                    if (x < 0 || y < 0 || x >= width || y >= height)
                    {
                        continue;
                    }

                    var c = bmp.GetPixel(x, y);
                    var key = c.ToArgb();
                    counts.TryGetValue(key, out var n);
                    counts[key] = n + 1;
                    sums.TryGetValue(key, out var s);
                    sums[key] = s ?? new long[3];
                    sums[key][0] += c.R;
                    sums[key][1] += c.G;
                    sums[key][2] += c.B;
                }

                var dominant = counts.OrderByDescending(kv => kv.Value).First();
                var avg = sums[dominant.Key];
                var count = dominant.Value;
                var rgb = Color.FromArgb((int)(avg[0] / count), (int)(avg[1] / count), (int)(avg[2] / count));
                Console.WriteLine($"  {edge} px{offset}: #{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}  ({(count * 100.0 / (end - start)):F0}%)");
            }
        }

        Console.WriteLine();
    }

    #region Native

    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, uint attributeSize);

    #endregion
}