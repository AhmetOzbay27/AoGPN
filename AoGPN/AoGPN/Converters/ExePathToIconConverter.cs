using System.Collections.Concurrent;
using System.Drawing;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AoGPN.Converters;

public class ExePathToIconConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<string, ImageSource> _cache = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var path = value as string;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null!;
        }

        if (_cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        try
        {
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null!;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(16, 16));
            source.Freeze();
            _cache[path] = source;
            return source;
        }
        catch
        {
            return null!;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
