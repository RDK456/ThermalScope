using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ThermalScope.Services;

/// <summary>
/// Builds the whole visual language at runtime from four independent choices: appearance (dark/light),
/// look (Scope, Glass, Depth, Flat), accent colour and gauge style. Every colour and look token is written
/// into the application resources, and the XAML consumes them with DynamicResource, so a change repaints
/// the running UI immediately.
/// </summary>
public static class LookManager
{
    public static readonly string[] Styles = { "Scope", "Glass", "Depth", "Flat" };
    public static readonly string[] GaugeModes = { "Ramp", "Zones", "Minimal" };

    public static readonly (string Name, Color Dark, Color Light)[] Accents =
    {
        ("Teal", Rgb(0x3DD6C6), Rgb(0x0A8A80)),
        ("Ember", Rgb(0xFF8A3D), Rgb(0xC2561A)),
        ("Violet", Rgb(0xA78BFA), Rgb(0x6D4AE0)),
        ("Lime", Rgb(0xB8F04A), Rgb(0x5E8A00)),
        ("Ice", Rgb(0x6CB8FF), Rgb(0x1F6FD1)),
        ("Mono", Rgb(0xE9EDF2), Rgb(0x14181D))
    };

    private sealed record Palette(
        Color Window, Color Panel, Color Raised, Color Line, Color Text, Color Muted, Color Faint, Color Load,
        Color Track, Color Danger, Color Warn, Color Banner, Color GlassTint, Color GlassEdge,
        Color DepthTop, Color DepthBottom, Color DepthRim, Color GridDot, double ShadowOpacity);

    private static readonly Palette Dark = new(
        Rgb(0x0D0F12), Rgb(0x14181D), Rgb(0x1B2027), Rgb(0x272D36), Rgb(0xE9EDF2), Rgb(0x9AA4B2), Rgb(0x7C8798),
        Rgb(0x8FB4FF), Rgb(0x232930), Rgb(0xFF5C7A), Rgb(0xF5B544), Rgb(0x2A2412),
        Argb(0x17FFFFFF), Argb(0x33FFFFFF), Rgb(0x232A34), Rgb(0x13171C), Argb(0x3AFFFFFF), Argb(0x16FFFFFF), 0.55);

    private static readonly Palette Light = new(
        Rgb(0xEEF1F5), Rgb(0xFFFFFF), Rgb(0xF3F5F8), Rgb(0xD9DEE6), Rgb(0x141A22), Rgb(0x4D5868), Rgb(0x66717F),
        Rgb(0x2B67D1), Rgb(0xE3E8EF), Rgb(0xC4163A), Rgb(0xA86200), Rgb(0xFFF6E5),
        Argb(0xA6FFFFFF), Argb(0xB3FFFFFF), Rgb(0xFFFFFF), Rgb(0xE4E9F0), Argb(0xCCFFFFFF), Argb(0x1A1F2937), 0.22);

    public static string Style { get; private set; } = "Scope";
    public static string Accent { get; private set; } = "Teal";
    public static string GaugeMode { get; private set; } = "Ramp";

    /// <summary>True while the window shows a Windows 11 system backdrop (Glass look on build 22621+).</summary>
    public static bool BackdropActive { get; private set; }

    public static event Action? Changed;

    public static void Apply(string style, string accent, string gaugeMode)
    {
        Style = Styles.Contains(style) ? style : "Scope";
        Accent = Accents.Any(a => a.Name == accent) ? accent : "Teal";
        GaugeMode = GaugeModes.Contains(gaugeMode) ? gaugeMode : "Ramp";
        Refresh();
    }

    public static void Refresh()
    {
        var resources = Application.Current.Resources;
        var p = ThemeManager.IsLight ? Light : Dark;

        resources["WindowBrush"] = Solid(p.Window);
        resources["PanelBrush"] = Solid(p.Panel);
        resources["RaisedBrush"] = Solid(p.Raised);
        resources["LineBrush"] = Solid(p.Line);
        resources["TextBrush"] = Solid(p.Text);
        resources["MutedBrush"] = Solid(p.Muted);
        resources["FaintBrush"] = Solid(p.Faint);
        resources["LoadBrush"] = Solid(p.Load);
        resources["TrackBrush"] = Solid(p.Track);
        resources["DangerBrush"] = Solid(p.Danger);
        resources["WarnBrush"] = Solid(p.Warn);
        resources["BannerBrush"] = Solid(p.Banner);

        var accent = Accents.First(a => a.Name == Accent);
        var accentColor = ThemeManager.IsLight ? accent.Light : accent.Dark;
        resources["AccentBrush"] = Solid(accentColor);
        resources["OnAccentBrush"] = Solid(Luminance(accentColor) > 0.42 ? Rgb(0x0B1212) : Colors.White);
        resources["GaugeMode"] = GaugeMode;

        switch (Style)
        {
            case "Scope":
                SetLook(resources, Solid(p.Panel), Solid(p.Line), new Thickness(1), 3, 3, null, true, ScopeBackdrop(p));
                break;
            case "Glass":
                SetLook(
                    resources, Solid(p.GlassTint), Solid(p.GlassEdge), new Thickness(1), 16, 10, null, false,
                    BackdropActive ? Brushes.Transparent : GlassFallback(p, accentColor));
                break;
            case "Depth":
                SetLook(
                    resources, Gradient(p.DepthTop, p.DepthBottom), Gradient(p.DepthRim, Colors.Transparent), new Thickness(1),
                    18, 12, new DropShadowEffect
                    {
                        Color = Colors.Black,
                        BlurRadius = 26,
                        ShadowDepth = 9,
                        Direction = 270,
                        Opacity = p.ShadowOpacity,
                        RenderingBias = RenderingBias.Performance
                    }, false, DepthBackdrop(p));
                break;
            default: // Flat
                SetLook(resources, Solid(p.Panel), Solid(Colors.Transparent), new Thickness(0), 12, 8, null, false, Solid(p.Window));
                break;
        }

        Changed?.Invoke();
    }

    /// <summary>Turns the Windows 11 acrylic backdrop on for the Glass look (and off otherwise). Returns whether it is active.</summary>
    public static bool ApplyBackdrop(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var dark = ThemeManager.IsLight ? 0 : 1;
        DwmSetWindowAttribute(handle, 20 /* USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));

        var wantGlass = Style == "Glass";
        var type = wantGlass ? 3 /* acrylic */ : 1 /* none */;
        var result = DwmSetWindowAttribute(handle, 38 /* SYSTEMBACKDROP_TYPE */, ref type, sizeof(int));
        var active = wantGlass && result == 0;

        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
        {
            target.BackgroundColor = active ? Colors.Transparent : (ThemeManager.IsLight ? Light.Window : Dark.Window);
        }

        if (System.Windows.Shell.WindowChrome.GetWindowChrome(window) is { } chrome)
        {
            chrome.GlassFrameThickness = active ? new Thickness(-1) : new Thickness(0);
        }

        // With the frame extended, DWM paints its own caption buttons over ours unless the system menu
        // style is off. Our own buttons drive WindowState directly, so nothing else depends on it.
        const int GwlStyle = -16;
        const long WsSysMenu = 0x00080000;
        var style = GetWindowLongPtr(handle, GwlStyle).ToInt64();
        var wanted = active ? style & ~WsSysMenu : style | WsSysMenu;
        if (wanted != style)
        {
            SetWindowLongPtr(handle, GwlStyle, new IntPtr(wanted));
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
        }

        if (active != BackdropActive)
        {
            BackdropActive = active;
            Refresh();
        }

        return active;
    }

    private static void SetLook(
        ResourceDictionary resources,
        Brush cardBackground,
        Brush cardBorder,
        Thickness cardBorderThickness,
        double cardRadius,
        double controlRadius,
        Effect? effect,
        bool reticle,
        Brush backdrop)
    {
        resources["CardBackground"] = cardBackground;
        resources["CardBorderBrush"] = cardBorder;
        resources["CardBorderThickness"] = cardBorderThickness;
        resources["CardCornerRadius"] = new CornerRadius(cardRadius);
        resources["ControlCornerRadius"] = new CornerRadius(controlRadius);
        resources["CardEffect"] = effect;
        resources["CardBracketsVisibility"] = reticle ? Visibility.Visible : Visibility.Collapsed;
        resources["WindowBackdrop"] = backdrop;
    }

    // Scope: a fine dot grid, like the reticle field of a viewfinder.
    private static Brush ScopeBackdrop(Palette p)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Solid(p.Window), null, new RectangleGeometry(new Rect(0, 0, 22, 22))));
        group.Children.Add(new GeometryDrawing(Solid(p.GridDot), null, new EllipseGeometry(new Point(11, 11), 0.9, 0.9)));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewbox = new Rect(0, 0, 22, 22),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 22, 22),
            ViewportUnits = BrushMappingMode.Absolute
        };
        brush.Freeze();
        return brush;
    }

    private static Brush DepthBackdrop(Palette p)
    {
        var brush = new RadialGradientBrush(Blend(p.Window, p.Panel, 0.75), p.Window)
        {
            Center = new Point(0.5, 0.15),
            GradientOrigin = new Point(0.5, 0.1),
            RadiusX = 0.95,
            RadiusY = 0.85
        };
        brush.Freeze();
        return brush;
    }

    private static Brush GlassFallback(Palette p, Color accent)
    {
        var brush = new LinearGradientBrush(p.Window, Blend(p.Window, accent, 0.12), 35);
        brush.Freeze();
        return brush;
    }

    private static Brush Gradient(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, 90);
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush Solid(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static Color Rgb(int value) => Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);

    private static Color Argb(uint value) =>
        Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
