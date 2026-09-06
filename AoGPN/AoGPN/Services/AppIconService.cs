using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AoGPN.Services;

/// <summary>
/// Resolves the Windows shell icon of an executable and hands it to the
/// dashboard as a small base64 PNG data URI — the same icon Explorer shows
/// for the file (the "original" app icon the user sees on the desktop).
///
/// The Game Boost / Connection Monitor tables display executables by name;
/// this service lets the renderer ask for the real icons of the paths it
/// shows. Icons are extracted once per path and cached in memory (the same
/// app usually reappears on every 2 s monitor tick, so the cache makes the
/// steady-state cost zero).
///
/// Extraction uses SHGetFileInfo (the shell's own icon resolution, including
/// the per-file-type generic icon when the path is missing or protected) with
/// System.Drawing.Icon.ExtractAssociatedIcon as a fallback. The result is
/// scaled to a fixed 64×64 PNG so the dashboard can reuse one payload for
/// table rows (24 px) and the Windows-style large-icon tiles (56 px).
/// </summary>
public sealed class AppIconService
{
    // SHGetFileInfo flags: SHGFI_ICON + SHGFI_LARGEICON + SHGFI_USEFILEATTRIBUTES.
    // USEFILEATTRIBUTES keeps the call working for missing/protected files
    // (it returns the generic icon of the file type instead of failing).
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    /// <summary>Max cached icons; a full clear keeps the cache bounded after long sessions.</summary>
    private const int CacheLimit = 512;

    /// <summary>Rendered size of the PNG payload (sharp for 24 px rows and 56 px tiles).</summary>
    private const int IconSize = 64;

    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Returns the cached icon data URI for <paramref name="exePath"/>, or null
    /// when no icon could be extracted (callers render their letter placeholder).
    /// </summary>
    public string? GetIconDataUri(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        var key = NormalizeKey(exePath);
        if (key.Length == 0)
        {
            return null;
        }

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var extracted = ExtractIconDataUri(key);
        if (extracted is null)
        {
            return null;
        }

        if (_cache.Count >= CacheLimit)
        {
            _cache.Clear();
        }

        _cache[key] = extracted;
        return extracted;
    }

    /// <summary>
    /// Resolves icons for many paths at once (the renderer asks for the paths
    /// its tables currently display). Paths without an extractable icon are
    /// simply absent from the result.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetIconDataUris(IEnumerable<string> paths)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (paths is null)
        {
            return result;
        }

        foreach (var path in paths)
        {
            var key = NormalizeKey(path);
            if (key.Length == 0 || result.ContainsKey(key))
            {
                continue;
            }

            var uri = GetIconDataUri(key);
            if (uri is not null)
            {
                result[key] = uri;
            }
        }

        return result;
    }

    private static string NormalizeKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.Trim();
        if (trimmed.Length > 320)
        {
            trimmed = trimmed[..320];
        }

        return trimmed;
    }

    private static string? ExtractIconDataUri(string exePath)
    {
        try
        {
            using var icon = ExtractShellIcon(exePath);
            if (icon is null)
            {
                return null;
            }

            using var bitmap = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(icon.ToBitmap(), 0, 0, IconSize, IconSize);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
        }
        catch
        {
            // Extraction is best-effort: a protected process path, a stale entry
            // or a GDI+ hiccup must never break the dashboard row rendering.
            return null;
        }
    }

    private static Icon? ExtractShellIcon(string exePath)
    {
        var info = new SHFILEINFO();
        var result = SHGetFileInfo(
            exePath,
            FILE_ATTRIBUTE_NORMAL,
            ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_USEFILEATTRIBUTES);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            // Fallback: the classic per-file icon extractor (works for most exes).
            try
            {
                return Icon.ExtractAssociatedIcon(exePath);
            }
            catch
            {
                return null;
            }
        }

        try
        {
            // Clone so the handle can be released immediately (Icon.FromHandle
            // does not own the handle).
            using (var fromHandle = Icon.FromHandle(info.hIcon))
            {
                return (Icon)fromHandle.Clone();
            }
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }
}