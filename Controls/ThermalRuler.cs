using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ThermalScope.ViewModels;

namespace ThermalScope.Controls;

/// <summary>
/// The heat ramp laid out as a ruler (20 to 110 degrees Celsius) with the current CPU temperature marked above
/// it and the GPU temperature below it, so both components read against the same scale at a glance.
/// </summary>
public sealed class ThermalRuler : FrameworkElement
{
    public static readonly DependencyProperty CpuFractionProperty = DependencyProperty.Register(
        nameof(CpuFraction), typeof(double), typeof(ThermalRuler),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GpuFractionProperty = DependencyProperty.Register(
        nameof(GpuFraction), typeof(double), typeof(ThermalRuler),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CpuTextProperty = DependencyProperty.Register(
        nameof(CpuText), typeof(string), typeof(ThermalRuler),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GpuTextProperty = DependencyProperty.Register(
        nameof(GpuText), typeof(string), typeof(ThermalRuler),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(ThermalRuler),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double CpuFraction { get => (double)GetValue(CpuFractionProperty); set => SetValue(CpuFractionProperty, value); }
    public double GpuFraction { get => (double)GetValue(GpuFractionProperty); set => SetValue(GpuFractionProperty, value); }
    public string CpuText { get => (string)GetValue(CpuTextProperty); set => SetValue(CpuTextProperty, value); }
    public string GpuText { get => (string)GetValue(GpuTextProperty); set => SetValue(GpuTextProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 120 || height < 50)
        {
            return;
        }

        const double side = 14;
        const double barHeight = 10;
        var barTop = Math.Round(height / 2 - barHeight / 2);
        var left = side;
        var right = width - side;
        double X(double fraction) => left + (right - left) * Math.Clamp(fraction, 0, 1);

        // Heat ramp bar.
        var stops = new GradientStopCollection();
        for (var i = 0; i <= 12; i++)
        {
            var f = i / 12.0;
            stops.Add(new GradientStop(Heat.ColorFor(Heat.GaugeMinimum + f * (Heat.GaugeMaximum - Heat.GaugeMinimum)), f));
        }

        var ramp = new LinearGradientBrush(stops, new Point(0, 0), new Point(1, 0));
        ramp.Freeze();
        dc.DrawRoundedRectangle(ramp, null, new Rect(left, barTop, right - left, barHeight), barHeight / 2, barHeight / 2);

        // Ticks every 10 degrees; labels at both ends.
        var tickPen = new Pen(LabelBrush, 1);
        tickPen.Freeze();
        for (var degrees = 20; degrees <= 110; degrees += 10)
        {
            var x = Math.Round(X((degrees - Heat.GaugeMinimum) / (Heat.GaugeMaximum - Heat.GaugeMinimum))) + 0.5;
            dc.DrawLine(tickPen, new Point(x, barTop + barHeight + 3), new Point(x, barTop + barHeight + (degrees % 30 == 20 ? 8 : 6)));
        }

        DrawText(dc, "20°", left, barTop + barHeight + 10, 11, LabelBrush, Align.Left, width);
        DrawText(dc, "110°", right, barTop + barHeight + 10, 11, LabelBrush, Align.Right, width);

        // CPU marker above the bar, GPU marker below it.
        DrawMarker(dc, CpuFraction, CpuText, "CPU", above: true, barTop, barHeight, X, width);
        DrawMarker(dc, GpuFraction, GpuText, "GPU", above: false, barTop, barHeight, X, width);
    }

    private void DrawMarker(
        DrawingContext dc,
        double fraction,
        string text,
        string label,
        bool above,
        double barTop,
        double barHeight,
        Func<double, double> toX,
        double width)
    {
        if (fraction < 0)
        {
            return;
        }

        var x = toX(fraction);
        var celsius = Heat.GaugeMinimum + fraction * (Heat.GaugeMaximum - Heat.GaugeMinimum);
        var brush = Heat.BrushFor(celsius);
        var tipY = above ? barTop - 2 : barTop + barHeight + 2;
        var baseY = above ? tipY - 7 : tipY + 7;
        var triangle = new StreamGeometry();
        using (var context = triangle.Open())
        {
            context.BeginFigure(new Point(x, tipY), true, true);
            context.LineTo(new Point(x - 5, baseY), false, false);
            context.LineTo(new Point(x + 5, baseY), false, false);
        }

        triangle.Freeze();
        dc.DrawGeometry(brush, null, triangle);

        var caption = string.IsNullOrEmpty(text) ? label : $"{label} {text}";
        // Above the bar the caption sits over the triangle; below the bar it clears the end-point labels.
        var y = above ? baseY - 18 : baseY + 12;
        DrawText(dc, caption, x, y, 12.5, brush, Align.Center, width);
    }

    private enum Align { Left, Center, Right }

    private void DrawText(DrawingContext dc, string text, double x, double y, double size, Brush brush, Align align, double clampWidth)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(TryFindResource("DisplayFont") as FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var drawX = align switch
        {
            Align.Center => x - formatted.Width / 2,
            Align.Right => x - formatted.Width,
            _ => x
        };
        drawX = Math.Clamp(drawX, 2, Math.Max(2, clampWidth - formatted.Width - 2));
        dc.DrawText(formatted, new Point(drawX, y));
    }
}
