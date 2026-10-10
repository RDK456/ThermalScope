using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ThermalScope.Models;

namespace ThermalScope.ViewModels;

/// <summary>Everything one hardware card (CPU or GPU) shows, already formatted for the UI.</summary>
public sealed class ComponentViewModel : Bindable
{
    private const int HistoryLength = 90;

    private readonly Queue<double> _usageHistory = new();
    private readonly Queue<double> _tempHistory = new();
    private double? _peakCelsius;

    private string _name = string.Empty;
    private string _usageText = "—";
    private string _tempValue = "--";
    private string _tempUnit = "°C";
    private double _fraction = -1;
    private Brush _tempBrush = Brushes.Gray;
    private Brush _overlayTempBrush = Brushes.Gray;
    private string _stateText = "Waiting";
    private Brush _stateBrush = Brushes.Gray;
    private bool _hasTemp;
    private string _hint = string.Empty;
    private string _sourceText = "—";
    private string _clockText = "—";
    private string _powerText = "—";
    private string _peakText = string.Empty;
    private double[] _usageSeries = Array.Empty<double>();
    private double[] _tempSeries = Array.Empty<double>();
    private string _automationName = string.Empty;
    private bool _isOnline;
    private double _peakFraction = -1;
    private bool _showInstallDriver;
    private bool _showRestartAdmin;

    public ComponentViewModel(string label, bool isCpu)
    {
        Label = label;
        IsCpu = isCpu;
        _name = isCpu ? "Detecting processor…" : "Detecting graphics adapter…";
    }

    public string Label { get; }
    public bool IsCpu { get; }
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string UsageText { get => _usageText; private set => Set(ref _usageText, value); }
    public string TempValue { get => _tempValue; private set => Set(ref _tempValue, value); }
    public string TempUnit { get => _tempUnit; private set => Set(ref _tempUnit, value); }
    public double Fraction { get => _fraction; private set => Set(ref _fraction, value); }
    public Brush TempBrush { get => _tempBrush; private set => Set(ref _tempBrush, value); }
    public Brush OverlayTempBrush { get => _overlayTempBrush; private set => Set(ref _overlayTempBrush, value); }
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public Brush StateBrush { get => _stateBrush; private set => Set(ref _stateBrush, value); }
    public bool HasTemp
    {
        get => _hasTemp;
        private set
        {
            if (Set(ref _hasTemp, value))
            {
                Raise(nameof(NoTemp));
            }
        }
    }

    public bool NoTemp => !_hasTemp;

    /// <summary>"42°C" for markers and labels, empty without a reading.</summary>
    public string TempShort => _hasTemp ? $"{_tempValue}{_tempUnit}" : string.Empty;

    /// <summary>Session-peak position on the dial (0..1), or -1 before any reading.</summary>
    public double PeakFraction { get => _peakFraction; private set => Set(ref _peakFraction, value); }

    /// <summary>Offers to install the PawnIO driver (CPU card only, when it is missing).</summary>
    public bool ShowInstallDriver { get => _showInstallDriver; internal set => Set(ref _showInstallDriver, value); }

    /// <summary>Offers a restart with administrator rights (CPU card only, when the driver is installed).</summary>
    public bool ShowRestartAdmin { get => _showRestartAdmin; internal set => Set(ref _showRestartAdmin, value); }
    public string Hint { get => _hint; private set => Set(ref _hint, value); }
    public string SourceText { get => _sourceText; private set => Set(ref _sourceText, value); }
    public string ClockText { get => _clockText; private set => Set(ref _clockText, value); }
    public string PowerText { get => _powerText; private set => Set(ref _powerText, value); }
    public string PeakText { get => _peakText; private set => Set(ref _peakText, value); }
    public double[] UsageSeries { get => _usageSeries; private set => Set(ref _usageSeries, value); }
    public double[] TempSeries { get => _tempSeries; private set => Set(ref _tempSeries, value); }
    public string AutomationName { get => _automationName; private set => Set(ref _automationName, value); }
    public bool IsOnline { get => _isOnline; private set => Set(ref _isOnline, value); }

    /// <param name="noTempHint">Why there is no temperature and what to do about it (empty when not applicable).</param>
    public void Update(MetricSnapshot metric, bool fahrenheit, string noTempHint)
    {
        Name = metric.Name;
        IsOnline = metric.UsagePercent.HasValue || metric.TemperatureCelsius.HasValue;
        UsageText = metric.UsagePercent.HasValue ? $"{metric.UsagePercent.Value:0}%" : "—";
        ClockText = FormatClock(metric.ClockMegahertz);
        PowerText = metric.PowerWatts.HasValue ? $"{metric.PowerWatts.Value:0.#} W" : "—";
        TempUnit = fahrenheit ? "°F" : "°C";

        if (metric.TemperatureCelsius is { } celsius)
        {
            HasTemp = true;
            TempValue = Convert(celsius, fahrenheit).ToString("0", CultureInfo.CurrentCulture);
            Fraction = Heat.Fraction(celsius);
            TempBrush = Heat.BrushFor(celsius);
            OverlayTempBrush = Heat.BrushFor(celsius, forceDark: true);
            StateText = Heat.StateFor(celsius);
            StateBrush = TempBrush;
            SourceText = metric.TemperatureLabel;
            Hint = string.Empty;
            _peakCelsius = Math.Max(_peakCelsius ?? celsius, celsius);
            PeakFraction = Heat.Fraction(_peakCelsius.Value);
            PeakText = $"Peak {Convert(_peakCelsius.Value, fahrenheit):0}{TempUnit}";
            AutomationName = $"{Label} temperature {TempValue} degrees {(fahrenheit ? "Fahrenheit" : "Celsius")}, {StateText}";
        }
        else
        {
            HasTemp = false;
            TempValue = "--";
            Fraction = -1;
            TempBrush = Neutral();
            OverlayTempBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA5, 0xB4));
            StateText = "No sensor";
            StateBrush = Neutral();
            SourceText = "—";
            Hint = noTempHint;
            PeakText = _peakCelsius.HasValue ? $"Peak {Convert(_peakCelsius.Value, fahrenheit):0}{TempUnit}" : string.Empty;
            AutomationName = $"{Label} temperature unavailable";
        }

        Raise(nameof(TempShort));
        Push(_usageHistory, metric.UsagePercent ?? double.NaN);
        Push(_tempHistory, metric.TemperatureCelsius ?? double.NaN);
        UsageSeries = _usageHistory.ToArray();
        TempSeries = _tempHistory.ToArray();
    }

    public static double Convert(double celsius, bool fahrenheit) => fahrenheit ? celsius * 9 / 5 + 32 : celsius;

    internal static Brush Neutral() => (Brush)Application.Current.FindResource("FaintBrush");

    private static string FormatClock(double? megahertz) => megahertz switch
    {
        null => "—",
        >= 1000 => $"{megahertz.Value / 1000:0.00} GHz",
        _ => $"{megahertz.Value:0} MHz"
    };

    private static void Push(Queue<double> queue, double value)
    {
        queue.Enqueue(value);
        while (queue.Count > HistoryLength)
        {
            queue.Dequeue();
        }
    }
}
