using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ThermalScope.Services;

namespace ThermalScope.Controls;

/// <summary>A panel whose frame (flat, outlined with reticle marks, glass, raised) comes from the active look.</summary>
public class Card : ContentControl
{
    // Styled by the implicit c:Card style in Themes/Theme.xaml.
}

/// <summary>
/// Viewfinder corner marks drawn just inside a card: the "scope" look. They use the accent colour and
/// repaint when the look or accent changes.
/// </summary>
public sealed class ReticleMarks : FrameworkElement
{
    public ReticleMarks()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => LookManager.Changed += OnLookChanged;
        Unloaded += (_, _) => LookManager.Changed -= OnLookChanged;
    }

    private void OnLookChanged() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 40 || height < 40)
        {
            return;
        }

        var brush = TryFindResource("AccentBrush") as Brush ?? Brushes.White;
        var pen = new Pen(brush, 1.6) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square };
        pen.Freeze();

        const double inset = 6;
        const double arm = 13;
        var l = inset;
        var t = inset;
        var r = width - inset;
        var b = height - inset;

        void Corner(double x, double y, double dx, double dy)
        {
            dc.DrawLine(pen, new Point(x, y + dy * arm), new Point(x, y));
            dc.DrawLine(pen, new Point(x, y), new Point(x + dx * arm, y));
        }

        Corner(l, t, +1, +1);
        Corner(r, t, -1, +1);
        Corner(l, b, +1, -1);
        Corner(r, b, -1, -1);
    }
}
