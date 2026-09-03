using System.Collections.Specialized;
using System.Windows.Media;

namespace AoGPN.Controls;

/// <summary>
/// Equirectangular world map rendered entirely with WPF primitives: a dotted land
/// mask (Natural Earth 110m, see <see cref="WorldMapLand"/>), a graticule, and
/// per-country dots sized by active connection count or, when available, by
/// per-country download/upload traffic from the clash API.
/// Re-renders whenever <see cref="Items"/> changes.
/// </summary>
public class WorldMapControl : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items),
        typeof(IEnumerable<CountryAggregateItem>),
        typeof(WorldMapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsChanged));

    public IEnumerable<CountryAggregateItem>? Items
    {
        get => (IEnumerable<CountryAggregateItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    private INotifyCollectionChanged? _observed;

    public WorldMapControl()
    {
        Unloaded += (_, _) => ObserveItems(null);
    }

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((WorldMapControl)d).ObserveItems(e.NewValue as System.Collections.IEnumerable);
    }

    private void ObserveItems(System.Collections.IEnumerable? source)
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnCollectionChanged;
            _observed = null;
        }

        if (source is INotifyCollectionChanged incc)
        {
            _observed = incc;
            incc.CollectionChanged += OnCollectionChanged;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }

    private static readonly Brush _background = Freeze(new SolidColorBrush(Color.FromRgb(0x0B, 0x12, 0x1E)));
    private static readonly Brush _graticuleBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen _graticulePen = Freeze(new Pen(_graticuleBrush, 1));
    private static readonly Brush _landBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0x8F, 0xA8, 0xC2)));
    private static readonly Brush _dotOutline = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush _labelBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)));
    private static readonly Typeface _labelTypeface = new("Segoe UI");

    private static readonly byte[] _land = DecodeLand();

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 10 || height <= 10)
        {
            return;
        }

        const double pad = 10;
        var mapWidth = width - pad * 2;
        var mapHeight = height - pad * 2;

        dc.DrawRectangle(_background, null, new Rect(0, 0, width, height));

        // Graticule every 30 degrees.
        for (var lon = -150; lon <= 150; lon += 30)
        {
            var x = pad + (lon + 180) / 360.0 * mapWidth;
            dc.DrawLine(_graticulePen, new Point(x, pad), new Point(x, pad + mapHeight));
        }
        for (var lat = -60; lat <= 60; lat += 30)
        {
            var y = pad + (90 - lat) / 180.0 * mapHeight;
            dc.DrawLine(_graticulePen, new Point(pad, y), new Point(pad + mapWidth, y));
        }

        // Land dots from the embedded 240x120 mask.
        var cellWidth = mapWidth / WorldMapLand.GridWidth;
        var cellHeight = mapHeight / WorldMapLand.GridHeight;
        var landRadius = Math.Max(0.6, Math.Min(cellWidth, cellHeight) * 0.34);
        for (var r = 0; r < WorldMapLand.GridHeight; r++)
        {
            for (var c = 0; c < WorldMapLand.GridWidth; c++)
            {
                if (IsLand(r, c))
                {
                    var x = pad + (c + 0.5) * cellWidth;
                    var y = pad + (r + 0.5) * cellHeight;
                    dc.DrawRectangle(_landBrush, null, new Rect(x - landRadius, y - landRadius, landRadius * 2, landRadius * 2));
                }
            }
        }

        var items = Items?.ToList() ?? [];
        var useTraffic = items.Any(x => x.TrafficBytes > 0);
        long Weight(CountryAggregateItem item) => useTraffic ? item.TrafficBytes : item.ConnectionCount;
        var maxWeight = items.Count > 0 ? items.Max(Weight) : 1;

        // Country dots: glow + core, sized by log(count or traffic).
        foreach (var item in items)
        {
            if (CountryCentroids.TryGet(item.CountryCode) is not { } coord)
            {
                continue;
            }

            var x = pad + (coord.Lon + 180) / 360.0 * mapWidth;
            var y = pad + (90 - coord.Lat) / 180.0 * mapHeight;
            var ratio = Weight(item) / (double)maxWeight;
            var radius = 2.5 + Math.Log10(Weight(item) + 1) * 3.2;

            var color = GetDotColor(ratio);
            var glow = Freeze(new RadialGradientBrush(Color.FromArgb(0x59, color.R, color.G, color.B), Color.FromArgb(0x00, color.R, color.G, color.B)));
            dc.DrawEllipse(glow, null, new Point(x, y), radius * 3.0, radius * 3.0);
            dc.DrawEllipse(new SolidColorBrush(color), new Pen(_dotOutline, 1), new Point(x, y), radius, radius);
        }

        // Labels for the busiest countries.
        foreach (var item in items.OrderByDescending(Weight).Take(6))
        {
            if (item.CountryCode.IsNullOrEmpty() || CountryCentroids.TryGet(item.CountryCode) is not { } coord)
            {
                continue;
            }

            var x = pad + (coord.Lon + 180) / 360.0 * mapWidth;
            var y = pad + (90 - coord.Lat) / 180.0 * mapHeight;
            var text = new FormattedText(
                item.CountryCode,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                _labelTypeface,
                10,
                _labelBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(x + 6, y - text.Height / 2));
        }
    }

    private static Color GetDotColor(double ratio)
    {
        // Cool (cyan) -> amber -> hot (red) with activity.
        var t = Math.Clamp(ratio, 0, 1);
        if (t < 0.5)
        {
            var f = t / 0.5;
            return Color.FromRgb(
                (byte)(0x34 + (0xE8 - 0x34) * f),
                (byte)(0xC8 + (0xC0 - 0xC8) * f),
                (byte)(0xF0 + (0x40 - 0xF0) * f));
        }

        var f2 = (t - 0.5) / 0.5;
        return Color.FromRgb(
            (byte)(0xE8 + (0xF0 - 0xE8) * f2),
            (byte)(0xC0 + (0x60 - 0xC0) * f2),
            (byte)(0x40 + (0x50 - 0x40) * f2));
    }

    private static bool IsLand(int row, int col)
    {
        var index = row * WorldMapLand.GridWidth + col;
        var byteIndex = index / 8;
        if (byteIndex >= _land.Length)
        {
            return false;
        }

        return (_land[byteIndex] & (0x80 >> (index % 8))) != 0;
    }

    private static byte[] DecodeLand()
    {
        try
        {
            return Convert.FromBase64String(WorldMapLand.Base64);
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }
}
