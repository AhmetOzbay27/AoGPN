using System.Windows;
using System.Windows.Media;

namespace AoGPN.Controls;

/// <summary>
/// A lightweight native "SVG-style" sparkline: a glowing gradient line with a soft
/// area fill. NaN samples (e.g. a timed-out ping) break the line into segments,
/// exactly like a gap in a real-time chart.
/// </summary>
public class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IList<double>),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush),
        typeof(Brush),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness),
        typeof(double),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Fixed chart ceiling; 0 = auto-scale to the highest sample.</summary>
    public static readonly DependencyProperty FixedMaxProperty = DependencyProperty.Register(
        nameof(FixedMax),
        typeof(double),
        typeof(Sparkline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IList<double>? Values
    {
        get => (IList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double FixedMax
    {
        get => (double)GetValue(FixedMaxProperty);
        set => SetValue(FixedMaxProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var values = Values;
        var w = ActualWidth;
        var h = ActualHeight;
        if (values is null || values.Count < 2 || w <= 0 || h <= 0)
        {
            return;
        }

        var max = FixedMax > 0
            ? FixedMax
            : values.Where(v => !double.IsNaN(v) && v > 0).DefaultIfEmpty(1).Max();
        if (max <= 0)
        {
            max = 1;
        }

        var n = values.Count;
        var step = w / (n - 1);
        var baseline = h - 2;
        var top = 2.0;

        // Build line + area geometry; a NaN sample starts a new segment (gap).
        var lineGeometry = new StreamGeometry();
        var areaGeometry = new StreamGeometry();
        using (var lineCtx = lineGeometry.Open())
        using (var areaCtx = areaGeometry.Open())
        {
            var started = false;
            for (var i = 0; i < n; i++)
            {
                var v = values[i];
                if (double.IsNaN(v) || v < 0)
                {
                    started = false;
                    continue;
                }

                var x = i * step;
                var y = top + (1 - Math.Min(1, v / max)) * (baseline - top);
                if (!started)
                {
                    lineCtx.BeginFigure(new Point(x, y), false, false);
                    areaCtx.BeginFigure(new Point(x, baseline), true, false);
                    areaCtx.LineTo(new Point(x, y), true, false);
                    started = true;
                }
                else
                {
                    lineCtx.LineTo(new Point(x, y), true, false);
                    areaCtx.LineTo(new Point(x, y), true, false);
                }
            }
            // Close every area figure back to its baseline so the fill is a clean wedge.
            if (started)
            {
                areaCtx.LineTo(new Point((n - 1) * step, baseline), true, false);
            }
            lineGeometry.Freeze();
            areaGeometry.Freeze();
        }

        // Soft area fill (vertical fade of the line color).
        if (LineBrush is SolidColorBrush solid)
        {
            var fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
            };
            fill.GradientStops.Add(new GradientStop(Color.FromArgb(90, solid.Color.R, solid.Color.G, solid.Color.B), 0));
            fill.GradientStops.Add(new GradientStop(Color.FromArgb(0, solid.Color.R, solid.Color.G, solid.Color.B), 1));
            fill.Freeze();
            dc.DrawGeometry(fill, null, areaGeometry);
        }

        // Glow pass.
        var glowPen = new Pen(LineBrush, StrokeThickness + 4)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        glowPen.Freeze();
        dc.PushOpacity(0.18);
        dc.DrawGeometry(null, glowPen, lineGeometry);
        dc.Pop();

        // Main line.
        var linePen = new Pen(LineBrush, StrokeThickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        linePen.Freeze();
        dc.DrawGeometry(null, linePen, lineGeometry);
    }
}
