using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ThermalScope.Models;
using ThermalScope.Services;

namespace ThermalScope.ViewModels;

public sealed class MainViewModel : Bindable
{
    public static readonly string[] SensorFilters = { "All", "CPU", "GPU", "Temps" };

    private readonly List<SensorReading> _lastSensors = new();
    private string _activeView = "Monitor";
    private bool _useFahrenheit;
    private string _sensorFilter = "All";
    private string _liveText = "CONNECTING";
    private Brush _liveBrush;
    private string _statusText = "Starting monitor…";
    private string _updatedText = string.Empty;
    private string _sensorCountText = "0 readings";
    private string _backendText = "Backend: starting…";
    private string _coverageText = "Waiting for a sample…";
    private string _noticeText = string.Empty;
    private string _cpuHint = string.Empty;
    private bool _showInstallDriver;
    private bool _showRestartAdmin;
    private MonitorSnapshot? _lastSnapshot;
    private bool _cpuNeedsDriver;
    private bool _driverInstalled;
    private bool _elevated;

    public MainViewModel()
    {
        _liveBrush = ComponentViewModel.Neutral();
        Cpu = new ComponentViewModel("CPU", isCpu: true);
        Gpu = new ComponentViewModel("GPU", isCpu: false);
        Pacer = new PacerViewModel();
        SensorsView = CollectionViewSource.GetDefaultView(Sensors);
        SensorsView.Filter = FilterSensor;
    }

    public ComponentViewModel Cpu { get; }
    public ComponentViewModel Gpu { get; }
    public PacerViewModel Pacer { get; }
    public ObservableCollection<SensorRowViewModel> Sensors { get; } = new();
    public ICollectionView SensorsView { get; }

    public string ActiveView
    {
        get => _activeView;
        set
        {
            if (Set(ref _activeView, value))
            {
                Raise(nameof(IsMonitorView));
                Raise(nameof(IsPacerView));
            }
        }
    }

    public bool IsMonitorView => _activeView == "Monitor";
    public bool IsPacerView => _activeView == "Pacer";

    public bool UseFahrenheit
    {
        get => _useFahrenheit;
        set
        {
            if (Set(ref _useFahrenheit, value) && _lastSnapshot is not null)
            {
                Apply(_lastSnapshot);
            }
        }
    }

    public string SensorFilter
    {
        get => _sensorFilter;
        set
        {
            if (Set(ref _sensorFilter, value))
            {
                SensorsView.Refresh();
            }
        }
    }

    public string LiveText { get => _liveText; private set => Set(ref _liveText, value); }
    public Brush LiveBrush { get => _liveBrush; private set => Set(ref _liveBrush, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string UpdatedText { get => _updatedText; private set => Set(ref _updatedText, value); }
    public string SensorCountText { get => _sensorCountText; private set => Set(ref _sensorCountText, value); }
    public string BackendText { get => _backendText; private set => Set(ref _backendText, value); }
    public string CoverageText { get => _coverageText; private set => Set(ref _coverageText, value); }
    public string NoticeText { get => _noticeText; private set => Set(ref _noticeText, value); }
    public bool HasNotice => !string.IsNullOrWhiteSpace(_noticeText);
    public string CpuHint { get => _cpuHint; private set => Set(ref _cpuHint, value); }
    public bool ShowInstallDriver { get => _showInstallDriver; private set => Set(ref _showInstallDriver, value); }
    public bool ShowRestartAdmin { get => _showRestartAdmin; private set => Set(ref _showRestartAdmin, value); }

    /// <summary>Tells the model what the machine can do, so the CPU card can say exactly what is missing.</summary>
    public void SetCapabilities(bool driverInstalled, bool elevated)
    {
        _driverInstalled = driverInstalled;
        _elevated = elevated;
        if (_lastSnapshot is not null)
        {
            Apply(_lastSnapshot);
        }
    }

    public void Apply(MonitorSnapshot snapshot)
    {
        _lastSnapshot = snapshot;
        _cpuNeedsDriver = snapshot.CpuSensorsNeedDriver;

        var hint = string.Empty;
        ShowInstallDriver = false;
        ShowRestartAdmin = false;
        if (_cpuNeedsDriver)
        {
            if (!_driverInstalled)
            {
                hint = "Windows itself only offers a motherboard temperature, which is not the CPU. Run MSI Afterburner (ThermalScope reads its real CPU die sensor, no extra driver) or install the signed PawnIO driver.";
                ShowInstallDriver = true;
            }
            else if (!_elevated)
            {
                hint = "The PawnIO driver is installed. Restart as administrator to read the real CPU die temperature.";
                ShowRestartAdmin = true;
            }
            else
            {
                hint = "This CPU did not report a die temperature through the PawnIO driver.";
            }
        }

        CpuHint = hint;
        Cpu.Update(snapshot.Cpu, _useFahrenheit, hint);
        Cpu.ShowInstallDriver = ShowInstallDriver;
        Cpu.ShowRestartAdmin = ShowRestartAdmin;
        Gpu.Update(snapshot.Gpu, _useFahrenheit, "The graphics driver did not report a temperature.");

        SyncSensors(snapshot.Sensors);
        SensorCountText = $"{snapshot.Sensors.Count} sensor{(snapshot.Sensors.Count == 1 ? string.Empty : "s")}";
        BackendText = snapshot.Source;
        CoverageText = BuildCoverage(snapshot);
        NoticeText = snapshot.Notice ?? string.Empty;
        Raise(nameof(HasNotice));

        var live = string.IsNullOrWhiteSpace(snapshot.Notice) && !_cpuNeedsDriver;
        LiveText = live ? "LIVE" : "LIMITED";
        LiveBrush = Heat.BrushFor(live ? 40 : 70);
        StatusText = live ? "Monitoring hardware" : "Monitoring with limited sensor access";
        UpdatedText = $"Updated {snapshot.Timestamp.ToLocalTime():HH:mm:ss}";
    }

    public void ReapplyTheme()
    {
        if (_lastSnapshot is not null)
        {
            Apply(_lastSnapshot);
        }

        Pacer.ReapplyTheme();
    }

    public void ReportBackendIssue(string message)
    {
        LiveText = "DEGRADED";
        LiveBrush = Heat.BrushFor(88);
        StatusText = "Sensor backend issue";
        NoticeText = message;
        Raise(nameof(HasNotice));
    }

    private void SyncSensors(IReadOnlyList<SensorReading> readings)
    {
        var counts = new Dictionary<string, int>();
        var keys = new List<string>(readings.Count);
        foreach (var reading in readings)
        {
            var baseKey = $"{reading.Component}|{reading.Category}|{reading.Name}";
            counts[baseKey] = counts.GetValueOrDefault(baseKey) + 1;
            keys.Add($"{baseKey}#{counts[baseKey]}");
        }

        CollectionSync.Sync(
            Sensors,
            readings,
            keys,
            row => row.Key,
            (reading, key) => new SensorRowViewModel(key, reading.Component, reading.Category, reading.Name),
            (row, reading) => row.Update(reading, _useFahrenheit));
    }

    private bool FilterSensor(object item)
    {
        var row = (SensorRowViewModel)item;
        return _sensorFilter switch
        {
            "CPU" => row.Component == "CPU",
            "GPU" => row.Component == "GPU",
            "Temps" => row.Category == nameof(LibreHardwareMonitor.Hardware.SensorType.Temperature),
            _ => true
        };
    }

    private static string BuildCoverage(MonitorSnapshot snapshot)
    {
        int Reporting(string component, string category) => snapshot.Sensors.Count(r =>
            r.Component == component && r.Category == category && r.Value.HasValue);
        int Total(string component, string category) => snapshot.Sensors.Count(r =>
            r.Component == component && r.Category == category);

        const string temperature = nameof(LibreHardwareMonitor.Hardware.SensorType.Temperature);
        return $"CPU temps {Reporting("CPU", temperature)}/{Total("CPU", temperature)} · GPU temps {Reporting("GPU", temperature)}/{Total("GPU", temperature)}";
    }
}

public sealed class PacerViewModel : Bindable
{
    private readonly ObservableCollection<ProcessRowViewModel> _processes = new();
    private GameMonitorSnapshot? _last;
    private string _gameName = "No game yet";
    private string _detectionText = "Start a game in borderless fullscreen and ThermalScope picks it up.";
    private bool _isAutoDetected;
    private bool _hasGame;
    private string _fpsText = "--";
    private string _frameTimeText = "—";
    private string _lowText = "—";
    private string _jitterText = "—";
    private string _pacingHint = string.Empty;
    private string _statusText = "Waiting for a game.";
    private string _errorText = string.Empty;
    private bool _consentRequired;
    private bool _protectedGame;
    private bool _needsAdmin;
    private string _targetInput = string.Empty;
    private bool _isEditing;
    private int _targetFps;
    private bool _limiterEnabled = true;
    private double[] _frameSeries = Array.Empty<double>();
    private double _graphMax = 40;
    private double _referenceMs = double.NaN;
    private string _stabilityText = "Collecting…";
    private Brush _stabilityBrush = Brushes.Gray;
    private Brush _overlayStabilityBrush = Brushes.Gray;
    private string _rangeText = string.Empty;
    private bool _isLive;
    private string _limitSummary = "No limit";
    private string _hotkeyText = string.Empty;
    private string _processCountText = string.Empty;

    public PacerViewModel()
    {
        _stabilityBrush = ComponentViewModel.Neutral();
    }

    public ObservableCollection<ProcessRowViewModel> Processes => _processes;
    public string GameName { get => _gameName; private set => Set(ref _gameName, value); }
    public string DetectionText { get => _detectionText; private set => Set(ref _detectionText, value); }
    public bool IsAutoDetected { get => _isAutoDetected; private set => Set(ref _isAutoDetected, value); }
    public bool HasGame { get => _hasGame; private set => Set(ref _hasGame, value); }
    public string FpsText { get => _fpsText; private set => Set(ref _fpsText, value); }
    public string FrameTimeText { get => _frameTimeText; private set => Set(ref _frameTimeText, value); }
    public string LowText { get => _lowText; private set => Set(ref _lowText, value); }
    public string JitterText { get => _jitterText; private set => Set(ref _jitterText, value); }
    public string PacingHint { get => _pacingHint; private set => Set(ref _pacingHint, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string ErrorText { get => _errorText; private set { if (Set(ref _errorText, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(_errorText);
    public bool ConsentRequired { get => _consentRequired; private set => Set(ref _consentRequired, value); }
    public bool ProtectedGame { get => _protectedGame; private set { if (Set(ref _protectedGame, value)) Raise(nameof(CanLimit)); } }
    public bool CanLimit => !_protectedGame;
    public bool NeedsAdmin { get => _needsAdmin; private set => Set(ref _needsAdmin, value); }
    public string TargetInput { get => _targetInput; set => Set(ref _targetInput, value); }
    public int TargetFps { get => _targetFps; private set => Set(ref _targetFps, value); }
    public bool LimiterEnabled { get => _limiterEnabled; private set => Set(ref _limiterEnabled, value); }
    public double[] FrameSeries
    {
        get => _frameSeries;
        private set
        {
            if (Set(ref _frameSeries, value))
            {
                Raise(nameof(NoGraph));
            }
        }
    }

    public bool NoGraph => _frameSeries.Length == 0;
    public double GraphMax { get => _graphMax; private set => Set(ref _graphMax, value); }
    public double ReferenceMs { get => _referenceMs; private set => Set(ref _referenceMs, value); }
    public string StabilityText { get => _stabilityText; private set => Set(ref _stabilityText, value); }
    public Brush StabilityBrush { get => _stabilityBrush; private set => Set(ref _stabilityBrush, value); }
    public Brush OverlayStabilityBrush { get => _overlayStabilityBrush; private set => Set(ref _overlayStabilityBrush, value); }
    public string RangeText { get => _rangeText; private set => Set(ref _rangeText, value); }
    public bool IsLive { get => _isLive; private set => Set(ref _isLive, value); }
    public string LimitSummary { get => _limitSummary; private set => Set(ref _limitSummary, value); }
    public string HotkeyText { get => _hotkeyText; set => Set(ref _hotkeyText, value); }
    public string ProcessCountText { get => _processCountText; private set => Set(ref _processCountText, value); }

    /// <summary>The text box should not be overwritten while the user is typing in it.</summary>
    public void SetEditing(bool editing) => _isEditing = editing;

    public void ReapplyTheme()
    {
        if (_last is not null)
        {
            Apply(_last);
        }
    }

    public void Apply(GameMonitorSnapshot snapshot)
    {
        _last = snapshot;
        HasGame = snapshot.SelectedExecutable is not null;
        IsAutoDetected = snapshot.AutoDetected;
        GameName = snapshot.SelectedExecutable ?? "No game yet";
        DetectionText = snapshot.SelectedExecutable is null
            ? "Start a game in borderless fullscreen and ThermalScope picks it up."
            : snapshot.AutoDetected ? "Detected automatically" : "Chosen by you";
        StatusText = snapshot.Status;
        ErrorText = snapshot.Error ?? string.Empty;
        ConsentRequired = snapshot.ConsentRequired;
        ProtectedGame = snapshot.ProtectedGame;
        NeedsAdmin = snapshot.NeedsAdmin;
        LimiterEnabled = snapshot.LimiterEnabled;
        TargetFps = snapshot.TargetFps;
        IsLive = snapshot.MonitorReady;
        LimitSummary = snapshot.TargetFps <= 0
            ? "No limit"
            : snapshot.LimiterEnabled ? $"{snapshot.TargetFps} FPS" : $"{snapshot.TargetFps} FPS (paused)";

        if (!_isEditing)
        {
            TargetInput = snapshot.TargetFps > 0 ? snapshot.TargetFps.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }

        var fps = snapshot.SelectedProcess?.Fps;
        FpsText = fps is { } sample ? sample.Average.ToString("0") : "--";
        FrameTimeText = fps is { } s2 ? $"{s2.FrameTimeMs:0.0} ms" : "—";
        LowText = fps is { } s3 && s3.Low > 0 ? $"{s3.Low:0}" : "—";

        PacingHint = DescribePacing(snapshot);
        UpdateGraph(snapshot);
        SyncProcesses(snapshot.VisibleProcesses);
    }

    private static string DescribePacing(GameMonitorSnapshot snapshot)
    {
        if (!snapshot.PacingLocked)
        {
            return "Precise timer: evenly spaced frames at any target. Works with every V-Sync / G-Sync setup.";
        }

        var hz = snapshot.RefreshHz;
        if (hz <= 0)
        {
            return "Locked to refresh: the display sets the pace for targets that divide its refresh rate. Waiting for a game.";
        }

        var rates = string.Join(", ", Enumerable.Range(1, 4).Select(n => ((double)hz / n).ToString("0.#", CultureInfo.InvariantCulture)));
        var target = snapshot.TargetFps;
        var divisor = target > 0 ? (hz + target / 2) / target : 0;
        var locked = divisor is >= 1 and <= 4 && Math.Abs((double)hz / divisor - target) <= 1.0;
        return locked
            ? $"Locked to your {hz} Hz display: every frame stays up for exactly {divisor} refresh{(divisor > 1 ? "es" : "")}."
            : $"Your display runs at {hz} Hz. Pick {rates} to lock; {(target > 0 ? target + " FPS uses the precise timer" : "no limit is set")}.";
    }

    private void UpdateGraph(GameMonitorSnapshot snapshot)
    {
        var frames = snapshot.FrameTimes;
        if (frames.Length < 2 || !snapshot.MonitorReady)
        {
            FrameSeries = Array.Empty<double>();
            StabilityText = snapshot.MonitorReady ? "Collecting…" : "No data yet";
            StabilityBrush = ComponentViewModel.Neutral();
            OverlayStabilityBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA5, 0xB4));
            RangeText = string.Empty;
            ReferenceMs = double.NaN;
            JitterText = "—";
            return;
        }

        var series = new double[frames.Length];
        for (var i = 0; i < frames.Length; i++)
        {
            series[i] = frames[i];
        }

        // Frame-to-frame spread: what the eye reads as (micro)stutter. Console-smooth delivery sits near 0.
        var mean = series.Average();
        JitterText = $"{Math.Sqrt(series.Sum(v => (v - mean) * (v - mean)) / series.Length):0.0} ms";

        var sorted = (double[])series.Clone();
        Array.Sort(sorted);
        var median = Math.Max(sorted[sorted.Length / 2], 0.5);
        var deviating = series.Count(value => Math.Abs(value - median) > median * 0.25);
        var ratio = (double)deviating / series.Length;

        if (series.Length < 30)
        {
            StabilityText = "Collecting…";
            StabilityBrush = ComponentViewModel.Neutral();
            OverlayStabilityBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA5, 0xB4));
        }
        else if (ratio < 0.03)
        {
            StabilityText = "Smooth";
            StabilityBrush = Heat.BrushFor(40);
            OverlayStabilityBrush = Heat.BrushFor(40, forceDark: true);
        }
        else if (ratio < 0.10)
        {
            StabilityText = "Minor stutter";
            StabilityBrush = Heat.BrushFor(72);
            OverlayStabilityBrush = Heat.BrushFor(72, forceDark: true);
        }
        else
        {
            StabilityText = "Stuttering";
            StabilityBrush = Heat.BrushFor(90);
            OverlayStabilityBrush = Heat.BrushFor(90, forceDark: true);
        }

        RangeText = series.Length >= 30
            ? $"↓{1000.0 / sorted[^1]:0} ↑{1000.0 / Math.Max(sorted[0], 0.1):0}"
            : string.Empty;
        FrameSeries = series;
        GraphMax = Math.Clamp(median * 3, 10, 120);
        ReferenceMs = snapshot.LimiterEnabled && snapshot.TargetFps > 0 ? 1000.0 / snapshot.TargetFps : double.NaN;
    }

    private void SyncProcesses(IReadOnlyList<GameProcessInfo> processes)
    {
        var distinct = processes
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        CollectionSync.Sync(
            _processes,
            distinct,
            distinct.Select(p => p.Name).ToList(),
            row => row.Name,
            (process, _) => new ProcessRowViewModel(process.Name),
            (row, process) => row.Update(process));
        ProcessCountText = $"{distinct.Count} running apps";
    }
}
