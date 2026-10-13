using System.Windows;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ThermalScope.Controls;

/// <summary>Look and content of the in-game overlay, chosen by the user.</summary>
public sealed record OverlayConfig(string Style, IReadOnlyCollection<string> Items, int Opacity)
{
    /// <summary>Changes whenever the visual tree has to be rebuilt.</summary>
    public string Key => $"{Style}|{Opacity}|{string.Join(',', Items.OrderBy(i => i))}";
}

/// <summary>
/// Builds the overlay visual from a config. Shared by the real overlay window and the preview in the app,
/// both bound to the main view model (Pacer.* for frame stats, Cpu.* / Gpu.* for the hardware strip).
/// The overlay is always dark and high contrast, whatever theme the app uses: it must read on top of any game.
/// </summary>
public static class OverlayContent
{
    public static readonly string[] Styles = { "Pill", "Panel", "Compact", "Text" };

    public static readonly string[] Positions =
    {
        "TopLeft", "TopCenter", "TopRight", "MiddleLeft", "MiddleRight", "BottomLeft", "BottomCenter", "BottomRight"
    };

    public static readonly (string Key, string Label)[] ItemList =
    {
        ("Fps", "FPS"), ("FrameTime", "Frame time"), ("Low", "Slowest frame"), ("Graph", "Frame graph"),
        ("CpuLoad", "CPU load"), ("CpuTemp", "CPU temp"), ("CpuPower", "CPU power"),
        ("GpuLoad", "GPU load"), ("GpuTemp", "GPU temp"), ("GpuPower", "GPU power")
    };

    public static readonly string[] DefaultItems = { "Fps", "Graph", "CpuLoad", "CpuTemp", "GpuLoad", "GpuTemp" };

    private static readonly Brush Text = Frozen("#F2F5F9");
    private static readonly Brush Muted = Frozen("#9AA5B4");
    private static readonly Brush Line = Frozen("#33FFFFFF");
    private static readonly Brush Edge = Frozen("#2EFFFFFF");

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    public static FrameworkElement Build(OverlayConfig config)
    {
        var on = new HashSet<string>(config.Items);
        var style = config.Style;
        var vertical = style is "Panel" or "Text";
        var small = style == "Compact";
        var fontSize = small ? 13.0 : 16.0;

        // Groups keep related readings together; the layout decides how groups are separated.
        var groups = new List<List<FrameworkElement>>();
        void Group(params FrameworkElement?[] cells)
        {
            var kept = cells.OfType<FrameworkElement>().ToList();
            if (kept.Count > 0)
            {
                groups.Add(kept);
            }
        }

        FrameworkElement? Cell(string key, string label, string path, double size) =>
            on.Contains(key) ? MakeCell(label, ValueText(path, size), vertical) : null;

        FrameworkElement? TempCell(string key, string label, string prefix, double size) =>
            on.Contains(key) ? MakeCell(label, TempText(prefix, size), vertical) : null;

        Group(
            Cell("Fps", "FPS", "Pacer.FpsText", small ? 16 : vertical ? 20 : 24),
            Cell("FrameTime", "FRAME", "Pacer.FrameTimeText", fontSize),
            Cell("Low", "LOW", "Pacer.LowText", fontSize));
        if (on.Contains("Graph"))
        {
            Group(Graph(vertical ? 190 : small ? 80 : 124, small ? 20 : vertical ? 34 : 26));
        }

        Group(
            Cell("CpuLoad", "CPU", "Cpu.UsageText", fontSize),
            TempCell("CpuTemp", vertical || !on.Contains("CpuLoad") ? "CPU TEMP" : string.Empty, "Cpu", fontSize),
            Cell("CpuPower", "CPU W", "Cpu.PowerText", fontSize));
        Group(
            Cell("GpuLoad", "GPU", "Gpu.UsageText", fontSize),
            TempCell("GpuTemp", vertical || !on.Contains("GpuLoad") ? "GPU TEMP" : string.Empty, "Gpu", fontSize),
            Cell("GpuPower", "GPU W", "Gpu.PowerText", fontSize));

        Panel root = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal };
        for (var g = 0; g < groups.Count; g++)
        {
            if (g > 0)
            {
                root.Children.Add(vertical
                    ? style == "Text"
                        ? new Border { Height = 4 }
                        : new Border { Height = 1, Background = Line, Margin = new Thickness(0, 7, 0, 7) }
                    : small
                        ? new Border { Width = 12 }
                        : new Border { Width = 1, Height = 20, Background = Line, Margin = new Thickness(16, 0, 16, 0) });
            }

            var group = new StackPanel { Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal };
            for (var i = 0; i < groups[g].Count; i++)
            {
                if (i > 0 && !vertical)
                {
                    groups[g][i].Margin = new Thickness(small ? 8 : 10, 0, 0, 0);
                }

                group.Children.Add(groups[g][i]);
            }

            root.Children.Add(group);
        }

        if (groups.Count == 0)
        {
            root.Children.Add(new TextBlock { Text = "Nothing selected", Foreground = Muted, FontSize = 13 });
        }

        var alpha = (byte)Math.Round(Math.Clamp(config.Opacity, 0, 100) * 2.55);
        var backing = style == "Text"
            ? Brushes.Transparent
            : new SolidColorBrush(Color.FromArgb(alpha, 0x0C, 0x0E, 0x12));
        var shell = new Border
        {
            Child = root,
            Background = backing,
            BorderBrush = style == "Text" ? Brushes.Transparent : Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(style switch { "Pill" => 22, "Compact" => 9, "Panel" => 12, _ => 0 }),
            Padding = style switch
            {
                "Pill" => new Thickness(20, 9, 20, 9),
                "Compact" => new Thickness(11, 5, 11, 5),
                "Panel" => new Thickness(14, 10, 14, 10),
                _ => new Thickness(2)
            },
            HorizontalAlignment = HorizontalAlignment.Center
        };

        if (style == "Text")
        {
            // No backing, so a soft dark halo keeps the text readable over bright scenes.
            root.Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 0, Opacity = 1, Color = Colors.Black };
        }

        return shell;
    }

    private static FrameworkElement MakeCell(string label, TextBlock value, bool vertical)
    {
        var caption = new TextBlock
        {
            Text = label,
            Foreground = Muted,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, vertical ? 18 : 6, 0)
        };

        if (vertical)
        {
            // Label left, value right: the columns line up row after row.
            var grid = new Grid { MinWidth = 190, Margin = new Thickness(0, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            value.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(value, 1);
            grid.Children.Add(caption);
            grid.Children.Add(value);
            return grid;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (label.Length > 0)
        {
            row.Children.Add(caption);
        }

        row.Children.Add(value);
        return row;
    }

    private static TextBlock ValueText(string path, double size)
    {
        var block = NewValue(size);
        block.SetBinding(TextBlock.TextProperty, new Binding(path) { Mode = BindingMode.OneWay });
        return block;
    }

    private static TextBlock TempText(string prefix, double size)
    {
        var block = NewValue(size);
        block.Inlines.Add(BoundRun($"{prefix}.TempValue"));
        block.Inlines.Add(BoundRun($"{prefix}.TempUnit"));
        block.SetBinding(TextBlock.ForegroundProperty, new Binding($"{prefix}.OverlayTempBrush"));
        return block;
    }

    private static Run BoundRun(string path)
    {
        var run = new Run();
        run.SetBinding(Run.TextProperty, new Binding(path) { Mode = BindingMode.OneWay });
        return run;
    }

    private static TextBlock NewValue(double size)
    {
        var block = new TextBlock
        {
            Foreground = Text,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center
        };
        block.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        return block;
    }

    private static FrameworkElement Graph(double width, double height)
    {
        var graph = new Sparkline
        {
            Width = width,
            Height = height,
            Minimum = 0,
            StrokeThickness = 1.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 0, 2)
        };
        graph.SetBinding(Sparkline.ValuesProperty, new Binding("Pacer.FrameSeries"));
        graph.SetBinding(Sparkline.MaximumProperty, new Binding("Pacer.GraphMax"));
        graph.SetBinding(Sparkline.StrokeProperty, new Binding("Pacer.OverlayStabilityBrush"));
        return graph;
    }
}
