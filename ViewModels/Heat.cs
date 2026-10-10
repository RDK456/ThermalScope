using System.Windows.Media;
using ThermalScope.Services;

namespace ThermalScope.ViewModels;

/// <summary>
/// The thermal colour ramp (teal, green, amber, vermilion, red). Colour always carries meaning:
/// the same temperature has the same colour everywhere, and every colour is paired with a word.
/// </summary>
public static class Heat
{
    public const double GaugeMinimum = 20;
    public const double GaugeMaximum = 110;

    private static readonly (double Celsius, Color Dark, Color Light)[] Stops =
    {
        (30, Color.FromRgb(0x3D, 0xD6, 0xC6), Color.FromRgb(0x0B, 0x8F, 0x84)),
        (55, Color.FromRgb(0x7E, 0xDC, 0x96), Color.FromRgb(0x2A, 0x8F, 0x4C)),
        (70, Color.FromRgb(0xF5, 0xB5, 0x44), Color.FromRgb(0xA8, 0x62, 0x00)),
        (85, Color.FromRgb(0xFF, 0x6A, 0x3D), Color.FromRgb(0xC8, 0x3E, 0x14)),
        (100, Color.FromRgb(0xFF, 0x3B, 0x5C), Color.FromRgb(0xBC, 0x12, 0x35))
    };

    private static readonly Dictionary<int, SolidColorBrush> Cache = new();

    public static Color ColorFor(double celsius, bool forceDark = false)
    {
        var light = !forceDark && ThemeManager.IsLight;
        if (celsius <= Stops[0].Celsius)
        {
            return light ? Stops[0].Light : Stops[0].Dark;
        }

        for (var i = 1; i < Stops.Length; i++)
        {
            if (celsius <= Stops[i].Celsius)
            {
                var from = Stops[i - 1];
                var to = Stops[i];
                var t = (celsius - from.Celsius) / (to.Celsius - from.Celsius);
                return Lerp(light ? from.Light : from.Dark, light ? to.Light : to.Dark, t);
            }
        }

        return light ? Stops[^1].Light : Stops[^1].Dark;
    }

    /// <param name="forceDark">Use the dark-theme palette (the in-game overlay is always dark).</param>
    public static Brush BrushFor(double celsius, bool forceDark = false)
    {
        var light = !forceDark && ThemeManager.IsLight;
        var key = (int)Math.Round(celsius) * 2 + (light ? 1 : 0);
        if (!Cache.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(ColorFor(celsius, forceDark));
            brush.Freeze();
            Cache[key] = brush;
        }

        return brush;
    }

    public static string StateFor(double celsius) => celsius switch
    {
        < 45 => "Cool",
        < 70 => "Normal",
        < 82 => "Warm",
        < 92 => "Hot",
        _ => "Critical"
    };

    public static double Fraction(double celsius) =>
        Math.Clamp((celsius - GaugeMinimum) / (GaugeMaximum - GaugeMinimum), 0, 1);

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));
}
