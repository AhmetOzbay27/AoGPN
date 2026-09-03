using System.Windows;
using System.Windows.Media;

namespace AoGPN.Controls;

/// <summary>
/// A circular gauge like the design's download/upload rings: a subtle full-circle
/// track plus a glowing sweep arc (rounded caps) that starts at 12 o'clock and
/// grows clockwise with <see cref="Value"/> (0..1).
/// </summary>
public class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color),
        typeof(Brush),
        typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(Brush),
        typeof(RingGauge),
        new FrameworkPropertyMetadata(new SolidColorBrush(System.Windows.Media.Color.FromArgb(18, 255, 255, 255)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness),
        typeof(double),
        typeof(RingGauge),
        new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush Color
    {
        get => (Brush)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var cx = ActualWidth / 2;
        var cy = ActualHeight / 2;
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - Thickness / 2 - 2;
        if (radius <= 0)
        {
            return;
        }

        var trackPen = new Pen(TrackBrush, Thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        trackPen.Freeze();

        var value = Math.Clamp(Value, 0, 1);
        var fillPen = new Pen(Color, Thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        fillPen.Freeze();

        // Track: full circle.
        dc.DrawGeometry(null, trackPen, CreateArc(new Point(cx, cy), radius, 0, 360));

        if (value <= 0.001)
        {
            return;
        }

        // Glow pass under the fill arc.
        var glowPen = new Pen(Color, Thickness + 4)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        glowPen.Freeze();
        dc.PushOpacity(0.28);
        dc.DrawGeometry(null, glowPen, CreateArc(new Point(cx, cy), radius, -90, 360 * value));
        dc.Pop();

        // Fill arc.
        dc.DrawGeometry(null, fillPen, CreateArc(new Point(cx, cy), radius, -90, 360 * value));
    }

    private static StreamGeometry CreateArc(Point center, double radius, double startAngle, double sweepAngle)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var start = PointOnCircle(center, radius, startAngle);
            ctx.BeginFigure(start, false, false);

            if (sweepAngle >= 360)
            {
                var mid = PointOnCircle(center, radius, startAngle + 180);
                ctx.ArcTo(mid, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                ctx.ArcTo(start, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            else if (sweepAngle > 0)
            {
                var end = PointOnCircle(center, radius, startAngle + sweepAngle);
                ctx.ArcTo(end, new Size(radius, radius), 0, sweepAngle > 180, SweepDirection.Clockwise, true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        return new Point(
            center.X + radius * Math.Sin(radians),
            center.Y - radius * Math.Cos(radians));
    }
}
