using System.Windows.Media;

namespace AoGPN.Converters;

public class MaterialDesignFonts
{
    /// <summary>
    /// Application resource key holding the current UI font family.
    /// Set once at startup (after config load) and updated live when the user changes it.
    /// </summary>
    public const string FontResourceKey = "AppFontFamily";

    /// <summary>
    /// Resolve the font family for the given configured name.
    /// An empty value returns the platform default (Segoe UI Variable Text on Windows).
    /// </summary>
    public static FontFamily GetFont(string? fontFamily)
    {
        if (fontFamily.IsNotEmpty())
        {
            // A bundled font shipped in the app fonts folder (e.g. Linux deployment)
            try
            {
                var fontPath = Utils.GetFontsPath();
                if (Directory.Exists(fontPath)
                    && Directory.EnumerateFiles(fontPath, "*.*", SearchOption.TopDirectoryOnly)
                        .Any(t => t.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                                  || t.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
                                  || t.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)))
                {
                    return new FontFamily(new Uri($@"file:///{fontPath}\"), $"./#{fontFamily}");
                }
            }
            catch
            {
            }
            // An installed system font with the same name (Windows)
            return new FontFamily(fontFamily);
        }
        return Utils.IsWindows()
            ? new FontFamily("Segoe UI Variable Text")
            : new FontFamily("Microsoft YaHei");
    }
}
