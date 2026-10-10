using System.Windows;
using System.Windows.Media;

namespace ThermalScope.Controls;

/// <summary>
/// A tiny line chart for live data: a stroked line over a soft area, optional dashed reference line
/// (for example the frame-time target). NaN values leave a gap instead of a false zero.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(object), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ReferenceValueProperty = DependencyProperty.Register(
        nameof(ReferenceValue), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ReferenceBrushProperty = DependencyProperty.Register(
        nameof(ReferenceBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>A double[] (typed as object: XAML cannot bind array-typed properties inside templates).</summary>
    public object? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double ReferenceValue
    {
        get => (double)GetValue(ReferenceValueProperty);
        set => SetValue(ReferenceValueProperty, value);
    }

    public Brush ReferenceBrush
    {
        get => (Brush)GetValue(ReferenceBrushProperty);
        set => SetValue(ReferenceBrushProperty, value);
    }

    /// <summary>Optional faint horizontal guides (three lines) to read the scale against.</summary>
    public Brush? GridBrush
    {
        get => (Brush?)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 2 || height <= 2)
        {
            return;
        }

        var range = Maximum - Minimum;
        if (range <= 0)
        {
            return;
        }

        const double inset = 2;
        double Y(double value) => inset + (height - 2 * inset) * (1 - Math.Clamp((value - Minimum) / range, 0, 1));

        if (GridBrush is { } gridBrush)
        {
            var gridPen = new Pen(gridBrush, 1);
            gridPen.Freeze();
            for (var i = 1; i <= 3; i++)
            {
                var y = Math.Round(height * i / 4) + 0.5;
                dc.DrawLine(gridPen, new Point(0, y), new Point(width, y));
            }
        }

        if (!double.IsNaN(ReferenceValue))
        {
            var pen = new Pen(ReferenceBrush, 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            var y = Math.Round(Y(ReferenceValue)) + 0.5;
            dc.DrawLine(pen, new Point(0, y), new Point(width, y));
        }

        var values = Values as double[];
        if (values is null || values.Length < 2)
        {
            return;
        }

        var step = width / (values.Length - 1);
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var lineContext = line.Open())
        using (var areaContext = area.Open())
        {
            var open = false;
            double lastX = 0;
            for (var i = 0; i < values.Length; i++)
            {
                if (double.IsNaN(values[i]))
                {
                    if (open)
                    {
                        areaContext.LineTo(new Point(lastX, height), false, false);
                        open = false;
                    }

                    continue;
                }

                var point = new Point(i * step, Y(values[i]));
                if (!open)
                {
                    lineContext.BeginFigure(point, false, false);
                    areaContext.BeginFigure(new Point(point.X, height), true, true);
                    areaContext.LineTo(point, false, false);
                    open = true;
                }
                else
                {
                    lineContext.LineTo(point, true, true);
                    areaContext.LineTo(point, false, false);
                }

                lastX = point.X;
            }

            if (open)
            {
                areaContext.LineTo(new Point(lastX, height), false, false);
            }
        }

        line.Freeze();
        area.Freeze();

        if (Stroke is SolidColorBrush solid)
        {
            var fill = new LinearGradientBrush(
                Color.FromArgb(70, solid.Color.R, solid.Color.G, solid.Color.B),
                Color.FromArgb(0, solid.Color.R, solid.Color.G, solid.Color.B),
                90);
            fill.Freeze();
            dc.DrawGeometry(fill, null, area);
        }

        var stroke = new Pen(Stroke, StrokeThickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, stroke, line);
    }
}
