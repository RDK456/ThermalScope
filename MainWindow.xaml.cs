using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ThermalScope.Models;
using ThermalScope.Services;

namespace ThermalScope;

public partial class MainWindow : Window
{
    private static readonly int[] RefreshIntervals = { 1, 2, 5, 10 };
    private readonly HardwareMonitorService _monitor = new();
    private readonly GameMonitorService _gameMonitor = new();
    private readonly PawnIoSetupManager _pawnIoSetup = new();
    private readonly ObservableCollection<SensorRow> _sensorRows = new();
    private readonly ObservableCollection<GameProcessRow> _gameProcessRows = new();
    private int _intervalIndex = 1;
    private bool _gameSettingsInitialized;
    private bool _suppressGameSelectionEvent;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
        SensorItems.ItemsSource = _sensorRows;
        GameProcessesList.ItemsSource = _gameProcessRows;
        _monitor.SnapshotReady += OnSnapshotReady;
        _monitor.MonitorError += OnMonitorError;
        _gameMonitor.SnapshotReady += OnGameSnapshotReady;
        _gameMonitor.MonitorError += OnGameMonitorError;
        _monitor.Start(RefreshIntervals[_intervalIndex]);
        _gameMonitor.Start();
        UpdateElevationHint();
        UpdateSetupButton();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateSetupButton();
        if (!_pawnIoSetup.ShouldPrompt)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "ThermalScope can read CPU package and per-core temperatures when the official PawnIO driver is installed. Windows will show an administrator prompt. Install it now?",
            "One-time CPU sensor setup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        _pawnIoSetup.MarkPrompted();
        if (answer == MessageBoxResult.Yes)
        {
            await RunPawnIoSetupAsync();
        }
    }

    private async void SetupButton_Click(object sender, RoutedEventArgs e)
    {
        await RunPawnIoSetupAsync();
    }

    private async Task<bool> RunPawnIoSetupAsync()
    {
        if (_isClosing || !SetupButton.IsEnabled)
        {
            return false;
        }

        SetupButton.IsEnabled = false;
        SetupButton.Content = "Setting up…";
        var result = await _pawnIoSetup.InstallAsync();
        if (_isClosing)
        {
            return false;
        }

        SetupButton.IsEnabled = true;
        SetupButton.Content = "Set up CPU sensors";
        if (!result.Success)
        {
            MessageBox.Show(
                this,
                result.Message,
                "PawnIO setup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        if (_pawnIoSetup.TryRestart())
        {
            Close();
            return true;
        }

        MessageBox.Show(
            this,
            "PawnIO setup completed. Restart ThermalScope to activate the CPU temperature sensors.",
            "Setup complete",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        UpdateSetupButton();
        return true;
    }

    private void UpdateSetupButton()
    {
        SetupButton.Visibility = _pawnIoSetup.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSnapshotReady(MonitorSnapshot snapshot)
    {
        if (_isClosing)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isClosing)
            {
                Render(snapshot);
            }
        }));
    }

    private void OnMonitorError(string message)
    {
        if (_isClosing)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isClosing)
            {
                return;
            }

            StatusText.Text = "Sensor backend issue";
            StatusText.Foreground = BrushFromHex("#F2B66D");
            HeaderStatusText.Text = "DEGRADED";
            HeaderStatusText.Foreground = BrushFromHex("#F2B66D");
            LiveDot.Fill = BrushFromHex("#F2B66D");
            FooterDot.Fill = BrushFromHex("#F2B66D");
            NoticeText.Text = message;
        }));
    }

    private void OnGameSnapshotReady(GameMonitorSnapshot snapshot)
    {
        if (_isClosing)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isClosing)
            {
                RenderGame(snapshot);
            }
        }));
    }

    private void OnGameMonitorError(string message)
    {
        if (_isClosing)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_isClosing)
            {
                GameStatusText.Text = $"Game monitor issue: {message}";
                GameStatusText.Foreground = BrushFromHex("#F2B66D");
            }
        }));
    }

    private void RenderGame(GameMonitorSnapshot snapshot)
    {
        FpsMonitorStatusText.Text = snapshot.FpsMonitorAvailable ? "DX11/DX12 READY" : "FPS MONITOR OFFLINE";
        FpsMonitorStatusText.Foreground = BrushFromHex(snapshot.FpsMonitorAvailable ? "#71DDB5" : "#F2B66D");
        FpsMonitorStatusText.ToolTip = snapshot.MonitorStatus;
        GameStatusText.Text = snapshot.Status;
        GameStatusText.Foreground = BrushFromHex(snapshot.Error is null ? "#D7E0F2" : "#F2B66D");
        GameCurrentFpsText.Text = snapshot.SelectedProcess?.Fps is { } fps
            ? $"{fps:0} FPS"
            : "—";
        GameCurrentFpsText.Foreground = BrushFromHex(snapshot.SelectedProcess?.Fps is null ? "#7E8BA5" : "#54D6C2");
        GameMonitorProcessText.Text = snapshot.SelectedExecutable ?? "No game selected";
        GameProcessCountText.Text = snapshot.VisibleProcesses.Count.ToString();

        if (!_gameSettingsInitialized)
        {
            AutoMonitorCheckBox.IsChecked = snapshot.AutoMonitor;
            _gameSettingsInitialized = true;
        }

        if (string.IsNullOrWhiteSpace(GameExecutableTextBox.Text) && !string.IsNullOrWhiteSpace(snapshot.SelectedExecutable))
        {
            GameExecutableTextBox.Text = snapshot.SelectedExecutable;
        }

        var previouslySelected = (GameProcessesList.SelectedItem as GameProcessRow)?.Name;
        _suppressGameSelectionEvent = true;
        try
        {
            _gameProcessRows.Clear();
            GameProcessRow? restoredSelection = null;
            foreach (var process in snapshot.VisibleProcesses)
            {
                var row = new GameProcessRow
                {
                    Name = process.Name,
                    Title = string.IsNullOrWhiteSpace(process.WindowTitle) ? "Running without a visible window" : process.WindowTitle,
                    Detail = process.Fps is { } processFps
                        ? $"{processFps:0} FPS · {Math.Max(0, process.WorkingSetBytes / 1024 / 1024)} MB"
                        : $"{Math.Max(0, process.WorkingSetBytes / 1024 / 1024)} MB"
                };
                _gameProcessRows.Add(row);
                if (string.Equals(row.Name, previouslySelected, StringComparison.OrdinalIgnoreCase))
                {
                    restoredSelection = row;
                }
            }

            if (restoredSelection is not null)
            {
                GameProcessesList.SelectedItem = restoredSelection;
            }
        }
        finally
        {
            _suppressGameSelectionEvent = false;
        }

        if (snapshot.SelectedExecutable is null)
        {
            GameMonitorStateText.Text = "No game selected";
        }
        else if (snapshot.MonitorAttached)
        {
            GameMonitorStateText.Text = "Standalone FPS monitor is attached.";
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.Error))
        {
            GameMonitorStateText.Text = snapshot.Error;
        }
        else if (!snapshot.FpsMonitorAvailable)
        {
            GameMonitorStateText.Text = "Run as administrator and use a 64-bit DirectX 11/12 game.";
        }
        else
        {
            GameMonitorStateText.Text = "Select the game process to monitor its FPS.";
        }
    }

    private void Render(MonitorSnapshot snapshot)
    {
        CpuNameText.Text = snapshot.Cpu.Name;
        GpuNameText.Text = snapshot.Gpu.Name;

        SetUsage(snapshot.Cpu.UsagePercent, CpuUsageText, CpuUsageBar);
        SetUsage(snapshot.Gpu.UsagePercent, GpuUsageText, GpuUsageBar);
        SetTemperature(snapshot.Cpu, CpuTempText, CpuTempStateText, CpuTempSourceText);
        SetTemperature(snapshot.Gpu, GpuTempText, GpuTempStateText, GpuTempSourceText);

        CpuClockText.Text = FormatGigahertz(snapshot.Cpu.ClockGigahertz);
        CpuPowerText.Text = FormatWatts(snapshot.Cpu.PowerWatts);
        GpuClockText.Text = FormatGigahertz(snapshot.Gpu.ClockGigahertz);
        GpuPowerText.Text = FormatWatts(snapshot.Gpu.PowerWatts);

        var cpuOnline = snapshot.Cpu.UsagePercent.HasValue || snapshot.Cpu.TemperatureCelsius.HasValue;
        var gpuOnline = snapshot.Gpu.UsagePercent.HasValue || snapshot.Gpu.TemperatureCelsius.HasValue;
        SetHealth(CpuHealthText, cpuOnline);
        SetHealth(GpuHealthText, gpuOnline);

        SummaryText.Text = $"CPU {FormatUsage(snapshot.Cpu.UsagePercent)}  ·  GPU {FormatUsage(snapshot.Gpu.UsagePercent)}";
        SensorCountText.Text = $"{snapshot.Sensors.Count} sensor{(snapshot.Sensors.Count == 1 ? string.Empty : "s")} detected";
        BackendText.Text = $"Backend: {snapshot.Source}";
        CoverageText.Text = BuildCoverageText(snapshot);
        NoticeText.Text = string.IsNullOrWhiteSpace(snapshot.Notice)
            ? "All available CPU/GPU sensors are being sampled."
            : snapshot.Notice;

        _sensorRows.Clear();
        foreach (var reading in snapshot.Sensors.Take(24))
        {
            _sensorRows.Add(new SensorRow
            {
                Name = reading.Name,
                Detail = $"{reading.Component} · {reading.Category}",
                Value = FormatSensorValue(reading),
                Unit = reading.Unit
            });
        }

        var isLive = string.IsNullOrWhiteSpace(snapshot.Notice);
        HeaderStatusText.Text = isLive ? "LIVE" : "DEGRADED";
        HeaderStatusText.Foreground = BrushFromHex(isLive ? "#7EE5BB" : "#F2B66D");
        LiveDot.Fill = BrushFromHex(isLive ? "#57D9A3" : "#F2B66D");
        FooterDot.Fill = BrushFromHex(isLive ? "#57D9A3" : "#F2B66D");
        StatusText.Text = isLive ? "Monitoring hardware" : "Monitoring with limited sensor access";
        StatusText.Foreground = BrushFromHex(isLive ? "#9BA8C0" : "#F2B66D");
        UpdatedText.Text = $"Updated {snapshot.Timestamp.ToLocalTime():HH:mm:ss}";
    }

    private static void SetUsage(double? value, TextBlock valueText, ProgressBar bar)
    {
        valueText.Text = FormatUsage(value);
        bar.Value = value.HasValue ? Math.Clamp(value.Value, 0, 100) : 0;
    }

    private static void SetTemperature(
        MetricSnapshot metric,
        TextBlock valueText,
        TextBlock stateText,
        TextBlock sourceText)
    {
        valueText.Text = metric.TemperatureCelsius.HasValue
            ? $"{metric.TemperatureCelsius.Value:0}°C"
            : "—";
        sourceText.Text = metric.TemperatureLabel;
        stateText.Text = metric.TemperatureCelsius switch
        {
            null => "N/A",
            <= 70 => "NORMAL",
            <= 85 => "WARM",
            _ => "HOT"
        };
        stateText.Foreground = BrushFromHex(metric.TemperatureCelsius switch
        {
            null => "#7E8BA5",
            <= 70 => "#71DDB5",
            <= 85 => "#F2B66D",
            _ => "#F38B9B"
        });
    }

    private static void SetHealth(TextBlock healthText, bool isOnline)
    {
        healthText.Text = isOnline ? "ONLINE" : "WAITING";
        healthText.Foreground = BrushFromHex(isOnline ? "#71DDB5" : "#7E8BA5");
    }

    private static string BuildCoverageText(MonitorSnapshot snapshot)
    {
        var temperatureSensors = snapshot.Sensors
            .Where(reading => reading.Category == nameof(LibreHardwareMonitor.Hardware.SensorType.Temperature))
            .ToList();
        var loadSensors = snapshot.Sensors
            .Where(reading => reading.Category == nameof(LibreHardwareMonitor.Hardware.SensorType.Load))
            .ToList();
        var cpuTemperatureSensors = temperatureSensors.Where(reading => reading.Component == "CPU").ToList();
        var gpuTemperatureSensors = temperatureSensors.Where(reading => reading.Component == "GPU").ToList();
        var cpuTemperatureAvailable = cpuTemperatureSensors.Count(reading => reading.Value.HasValue);
        var gpuTemperatureAvailable = gpuTemperatureSensors.Count(reading => reading.Value.HasValue);
        var loadAvailable = loadSensors.Count(reading => reading.Value.HasValue);
        return $"CPU {cpuTemperatureAvailable}/{cpuTemperatureSensors.Count} temp · GPU {gpuTemperatureAvailable}/{gpuTemperatureSensors.Count} temp · {loadAvailable}/{loadSensors.Count} load sensors reporting";
    }

    private static string FormatUsage(double? value) => value.HasValue ? $"{value.Value:0}%" : "—";

    private static string FormatGigahertz(double? value) => value.HasValue ? $"{value.Value:0.0} GHz" : "—";

    private static string FormatWatts(double? value) => value.HasValue ? $"{value.Value:0.0} W" : "—";

    private static string FormatSensorValue(SensorReading reading)
    {
        if (!reading.Value.HasValue)
        {
            return "—";
        }

        return reading.Category == nameof(LibreHardwareMonitor.Hardware.SensorType.Clock)
            ? $"{reading.Value.Value:0}"
            : $"{reading.Value.Value:0.0}";
    }

    private static SolidColorBrush BrushFromHex(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    private void UpdateElevationHint()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            AdminHintText.Text = principal.IsInRole(WindowsBuiltInRole.Administrator)
                ? "Elevated sensor access enabled"
                : "Tip: run as administrator for full sensor access";
        }
        catch
        {
            AdminHintText.Text = "Tip: run as administrator for full sensor access";
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindButtonAncestor(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Normal ? WindowState.Maximized : WindowState.Normal;
        }
        else
        {
            DragMove();
        }
    }

    private static Button? FindButtonAncestor(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button button)
            {
                return button;
            }

            try
            {
                element = VisualTreeHelper.GetParent(element);
            }
            catch (InvalidOperationException)
            {
                element = LogicalTreeHelper.GetParent(element);
            }
        }

        return null;
    }

    private void MonitorGameButton_Click(object sender, RoutedEventArgs e)
    {
        var executable = GameExecutableTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(executable))
        {
            MessageBox.Show(
                this,
                "Enter the game executable name, for example Game.exe.",
                "Select a game",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var autoMonitor = AutoMonitorCheckBox.IsChecked == true;
        _gameMonitor.Configure(executable, autoMonitor);
        GameStatusText.Text = $"Watching {executable} for FPS…";
        GameStatusText.Foreground = BrushFromHex("#D7E0F2");
    }

    private void StopGameMonitorButton_Click(object sender, RoutedEventArgs e)
    {
        _gameMonitor.Clear();
        GameExecutableTextBox.Clear();
        AutoMonitorCheckBox.IsChecked = false;
        GameStatusText.Text = "Game FPS monitor stopped.";
        GameStatusText.Foreground = BrushFromHex("#D7E0F2");
        GameMonitorStateText.Text = "No game selected";
    }

    private void GameProcessesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressGameSelectionEvent)
        {
            return;
        }

        if (GameProcessesList.SelectedItem is GameProcessRow row)
        {
            GameExecutableTextBox.Text = row.Name;
            GameStatusText.Text = $"{row.Name} selected. Click Monitor FPS to attach.";
            GameStatusText.Foreground = BrushFromHex("#D7E0F2");
        }
    }

    private void IntervalButton_Click(object sender, RoutedEventArgs e)
    {
        _intervalIndex = (_intervalIndex + 1) % RefreshIntervals.Length;
        var seconds = RefreshIntervals[_intervalIndex];
        _monitor.SetInterval(seconds);
        IntervalButton.Content = $"{seconds} sec";
        StatusText.Text = $"Refresh interval set to {seconds} seconds";
        StatusText.Foreground = BrushFromHex("#9BA8C0");
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _monitor.RequestRefresh();
        StatusText.Text = "Refresh queued";
        StatusText.Foreground = BrushFromHex("#9BA8C0");
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
        else if (e.Key == Key.F5)
        {
            _monitor.RequestRefresh();
            _gameMonitor.RequestRefresh();
            e.Handled = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _isClosing = true;
        _monitor.SnapshotReady -= OnSnapshotReady;
        _monitor.MonitorError -= OnMonitorError;
        _gameMonitor.SnapshotReady -= OnGameSnapshotReady;
        _gameMonitor.MonitorError -= OnGameMonitorError;
        _monitor.Dispose();
        _gameMonitor.Dispose();
        base.OnClosing(e);
    }
}

public sealed class SensorRow
{
    public string Name { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Value { get; init; } = "—";
    public string Unit { get; init; } = string.Empty;
}

public sealed class GameProcessRow
{
    public string Name { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}
