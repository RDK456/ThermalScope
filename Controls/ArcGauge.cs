using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ThermalScope.Services;
using ThermalScope.ViewModels;

namespace ThermalScope.Controls;

/// <summary>
/// A 270° dial with three looks. Ramp: the whole ring is the heat ramp (lit up to the reading, dim beyond it),
/// with scale ticks, a session-peak mark and a reading dot. Zones: a tinted track with the value arc on top.
/// Minimal: a plain track and arc. The value arc always takes the colour of the current temperature.
/// </summary>
public sealed class ArcGauge : FrameworkElement
{
    private const double StartAngle = 135;
    private const double Sweep = 270;
    private const int RampSegments = 54;

    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(ArcGauge),
        new PropertyMetadata(-1.0, (d, e) => ((ArcGauge)d).OnFractionChanged((double)e.NewValue)));

    public static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        nameof(Shown), typeof(double), typeof(ArcGauge),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PeakFractionProperty = DependencyProperty.Register(
        nameof(PeakFraction), typeof(double), typeof(ArcGauge),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ArcBrushProperty = DependencyProperty.Register(
        nameof(ArcBrush), typeof(Brush), typeof(ArcGauge),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ArcGauge),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TickBrushProperty = DependencyProperty.Register(
        nameof(TickBrush), typeof(Brush), typeof(ArcGauge),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ArcGauge),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(string), typeof(ArcGauge),
        new FrameworkPropertyMetadata("Zones", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0..1 position of the needle, or a negative number when there is no reading.</summary>
    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }

    private double Shown { get => (double)GetValue(ShownProperty); set => SetValue(ShownProperty, value); }

    public double PeakFraction { get => (double)GetValue(PeakFractionProperty); set => SetValue(PeakFractionProperty, value); }
    public Brush ArcBrush { get => (Brush)GetValue(ArcBrushProperty); set => SetValue(ArcBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public Brush TickBrush { get => (Brush)GetValue(TickBrushProperty); set => SetValue(TickBrushProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    /// <summary>"Ramp", "Zones" or "Minimal".</summary>
    public string Mode { get => (string)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }

    private void OnFractionChanged(double target)
    {
        BeginAnimation(ShownProperty, null);
        if (target < 0 || Shown < 0 || !SystemParameters.ClientAreaAnimation)
        {
            Shown = target;
            return;
        }

        BeginAnimation(ShownProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var thickness = Thickness;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 - thickness / 2 - 10;
        if (radius <= 0)
        {
            return;
        }

        var shown = Shown;
        switch (Mode)
        {
            case "Ramp":
                DrawRamp(dc, center, radius, thickness, shown);
                break;
            case "Minimal":
                DrawTrack(dc, center, radius, thickness);
                DrawValue(dc, center, radius, thickness, shown);
                break;
            default:
                DrawZones(dc, center, radius, thickness);
                DrawValue(dc, center, radius, thickness, shown);
                break;
        }
    }

    // Segment geometry and brushes only change with size, thickness or theme, so build them once instead of
    // on every render (the dial re-renders on every frame of the value animation).
    private Geometry[]? _rampGeometry;
    private Pen[]? _rampLit;
    private Pen[]? _rampDim;
    private (double Radius, double Ring, bool Light, Point Center) _rampKey;

    private void EnsureRamp(Point center, double radius, double ringThickness)
    {
        var key = (radius, ringThickness, ThemeManager.IsLight, center);
        if (_rampGeometry is not null && key.Equals(_rampKey))
        {
            return;
        }

        _rampKey = key;
        _rampGeometry = new Geometry[RampSegments];
        _rampLit = new Pen[RampSegments];
        _rampDim = new Pen[RampSegments];
        for (var i = 0; i < RampSegments; i++)
        {
            double from = (double)i / RampSegments;
            double to = (double)(i + 1) / RampSegments;
            var color = Heat.ColorFor(Heat.GaugeMinimum + (from + to) / 2 * (Heat.GaugeMaximum - Heat.GaugeMinimum));
            var lit = new SolidColorBrush(color);
            var dim = new SolidColorBrush(color) { Opacity = 0.17 };
            lit.Freeze();
            dim.Freeze();
            var litPen = new Pen(lit, ringThickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            var dimPen = new Pen(dim, ringThickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            litPen.Freeze();
            dimPen.Freeze();
            _rampLit[i] = litPen;
            _rampDim[i] = dimPen;
            // Tiny overlap hides hairline seams between neighbouring segments.
            _rampGeometry[i] = Arc(center, radius, from, Math.Min(1, to + 0.004));
        }
    }

    private void DrawRamp(DrawingContext dc, Point center, double radius, double thickness, double shown)
    {
        var ringThickness = Math.Max(5, thickness * 0.62);
        EnsureRamp(center, radius, ringThickness);
        for (var i = 0; i < RampSegments; i++)
        {
            var lit = shown >= 0 && (double)i / RampSegments < shown;
            dc.DrawGeometry(null, lit ? _rampLit![i] : _rampDim![i], _rampGeometry![i]);
        }
        // Scale ticks every 10 degrees, just outside the ring.
        var tickPen = new Pen(TickBrush, 1);
        tickPen.Freeze();
        for (var degrees = 20; degrees <= 110; degrees += 10)
        {
            var f = (degrees - Heat.GaugeMinimum) / (Heat.GaugeMaximum - Heat.GaugeMinimum);
            var major = degrees % 30 == 20;
            dc.DrawLine(
                tickPen,
                PointAt(center, radius + ringThickness / 2 + 3, StartAngle + Sweep * f),
                PointAt(center, radius + ringThickness / 2 + (major ? 9 : 6), StartAngle + Sweep * f));
        }

        // Session peak: a white notch across the ring.
        if (PeakFraction >= 0)
        {
            var peakPen = new Pen(Brushes.White, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            peakPen.Freeze();
            var angle = StartAngle + Sweep * Math.Clamp(PeakFraction, 0, 1);
            dc.DrawLine(
                peakPen,
                PointAt(center, radius - ringThickness / 2 - 2, angle),
                PointAt(center, radius + ringThickness / 2 + 2, angle));
        }

        // Reading dot on the ring.
        if (shown >= 0)
        {
            var at = PointAt(center, radius, StartAngle + Sweep * Math.Clamp(shown, 0, 1));
            dc.DrawEllipse(Brushes.White, new Pen(ArcBrush, 2.5), at, ringThickness * 0.5, ringThickness * 0.5);
        }
    }

    private void DrawZones(DrawingContext dc, Point center, double radius, double thickness)
    {
        // Zone boundaries in gauge fractions: cool | warm | hot | critical.
        double[] edges = { 0, 0.40, 0.65, 0.85, 1 };
        double[] midTemps = { 40, 66, 86, 104 };
        for (var i = 0; i < midTemps.Length; i++)
        {
            var brush = new SolidColorBrush(Heat.ColorFor(midTemps[i])) { Opacity = 0.22 };
            brush.Freeze();
            var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            dc.DrawGeometry(null, pen, Arc(center, radius, edges[i], edges[i + 1]));
        }
    }

    private void DrawTrack(DrawingContext dc, Point center, double radius, double thickness)
    {
        var track = new Pen(TrackBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, track, Arc(center, radius, 0, 1));
    }

    private void DrawValue(DrawingContext dc, Point center, double radius, double thickness, double shown)
    {
        if (shown < 0)
        {
            return;
        }

        shown = Math.Clamp(shown, 0.004, 1);
        var valuePen = new Pen(ArcBrush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.DrawGeometry(null, valuePen, Arc(center, radius, 0, shown));
    }

    private static Geometry Arc(Point center, double radius, double from, double to)
    {
        var start = PointAt(center, radius, StartAngle + Sweep * from);
        var end = PointAt(center, radius, StartAngle + Sweep * to);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(end, new Size(radius, radius), 0, (to - from) * Sweep > 180, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Point PointAt(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
