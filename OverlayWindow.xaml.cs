using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ThermalScope.Controls;

namespace ThermalScope;

/// <summary>
/// A minimal, rounded, centred strip (FPS, frame line, CPU/GPU load and temperature) drawn inside the game's
/// own window, at its top centre. It follows the window if the game moves or resizes, scales with the window
/// (or a fixed size from settings), never grows wider than the window, is click-through and never takes focus.
/// Exclusive-fullscreen games hide it, like every window-based overlay.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private const double ReferenceWidth = 1920.0; // a 1080p-wide window is "100%"
    private static readonly IntPtr HwndTopmost = new(-1);

    private IntPtr _handle;
    private double _appliedScale;
    private int _lastX = int.MinValue;
    private int _lastY = int.MinValue;
    private int _lastGameWidth;

    public OverlayWindow(object viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SizeChanged += (_, _) => _lastX = int.MinValue; // content width changed: centre again on the next tick
    }

    /// <summary>"Auto" (follows the game window) or a fixed percentage such as "130%".</summary>
    public string ScaleSetting { get; set; } = "Auto";

    /// <summary>One of OverlayContent.Positions: where in the game window the overlay sits.</summary>
    public string Position { get; set; } = "TopCenter";

    private string? _configKey;

    /// <summary>Rebuilds the visual when the user's style, items or opacity changed.</summary>
    public void Configure(OverlayConfig config)
    {
        if (config.Key == _configKey)
        {
            return;
        }

        _configKey = config.Key;
        Pill.Child = OverlayContent.Build(config);
        _appliedScale = 0;
        _lastX = int.MinValue;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;

        // Click-through, no focus, and out of Alt+Tab.
        var style = GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(_handle, GwlExStyle, new IntPtr(style | WsExTransparent | WsExNoActivate | WsExToolWindow));
    }

    /// <summary>Shows the strip inside the game's window; hides it when that window is gone or minimised.</summary>
    public void ShowFor(int gameProcessId)
    {
        var gameWindow = FindGameWindow(gameProcessId);
        if (gameWindow == IntPtr.Zero || IsIconic(gameWindow) || !GetWindowRect(gameWindow, out var rect))
        {
            HideOverlay();
            return;
        }

        var gameWidth = rect.Right - rect.Left;
        if (gameWidth < 240 || rect.Bottom - rect.Top < 160)
        {
            HideOverlay();
            return;
        }

        if (!IsVisible)
        {
            Show();
            _lastX = int.MinValue;
        }

        Opacity = 1;
        ApplyScale(gameWidth, rect.Bottom - rect.Top);
        Reposition(rect);
    }

    public void HideOverlay()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    /// <summary>
    /// For when the player only looks elsewhere (another monitor, alt-tab): make the strip invisible but keep the
    /// window. Hiding and re-showing a topmost window makes Windows recompose the game's display, which costs frames.
    /// </summary>
    public void DimOverlay()
    {
        if (IsVisible)
        {
            Opacity = 0;
        }
    }

    /// <summary>True when the process owns the window the user is looking at.</summary>
    public static bool IsForeground(int processId)
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(window, out var owner);
        return owner == processId;
    }

    private static IntPtr FindGameWindow(int processId)
    {
        // The window in front is the one the player sees; it is the game's whenever IsForeground() was true.
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero)
        {
            GetWindowThreadProcessId(foreground, out var owner);
            if (owner == processId)
            {
                return foreground;
            }
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainWindowHandle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private void ApplyScale(int gameWidthPixels, int gameHeightPixels)
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double scale;
        if (ScaleSetting.EndsWith('%') &&
            double.TryParse(ScaleSetting.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            scale = percent / 100.0;
        }
        else
        {
            // Physical size proportional to the game window width, converted to device-independent units.
            scale = Math.Clamp(gameWidthPixels / ReferenceWidth, 0.7, 2.5) / dpi;
        }

        if (Math.Abs(scale - _appliedScale) >= 0.01 || gameWidthPixels != _lastGameWidth)
        {
            Pill.LayoutTransform = new ScaleTransform(scale, scale);
            Pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            // Never larger than the game window: shrink to fit (the vertical styles are tall, the pill is wide).
            var widthPixels = Pill.DesiredSize.Width * dpi;
            var heightPixels = Pill.DesiredSize.Height * dpi;
            var fit = Math.Min(gameWidthPixels * 0.96 / Math.Max(widthPixels, 1), gameHeightPixels * 0.9 / Math.Max(heightPixels, 1));
            if (fit < 1)
            {
                scale *= fit;
                Pill.LayoutTransform = new ScaleTransform(scale, scale);
            }

            _appliedScale = scale;
            _lastGameWidth = gameWidthPixels;
            _lastX = int.MinValue;
        }
    }

    private void Reposition(Rect32 game)
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        UpdateLayout();
        GetWindowRect(_handle, out var own);
        var width = own.Right - own.Left;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var height = own.Bottom - own.Top;
        var gameWidth = game.Right - game.Left;
        var gameHeight = game.Bottom - game.Top;
        var margin = (int)Math.Round(6 * dpi); // the Pill's own 10 px margin adds to this
        var x = Position.EndsWith("Left", StringComparison.Ordinal) ? game.Left + margin
            : Position.EndsWith("Right", StringComparison.Ordinal) ? game.Right - width - margin
            : game.Left + Math.Max(0, (gameWidth - width) / 2);
        var y = Position.StartsWith("Top", StringComparison.Ordinal) ? game.Top + margin
            : Position.StartsWith("Bottom", StringComparison.Ordinal) ? game.Bottom - height - margin
            : game.Top + Math.Max(0, (gameHeight - height) / 2);
        if (x == _lastX && y == _lastY)
        {
            return; // nothing moved: skip the SetWindowPos
        }

        _lastX = x;
        _lastY = y;
        SetWindowPos(_handle, HwndTopmost, x, y, 0, 0, SwpNoSize | SwpNoActivate);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
