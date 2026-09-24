using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ThermalScope.Models;

namespace ThermalScope.Services;

public sealed class GameMonitorService : IDisposable
{
    private readonly object _gate = new();
    private readonly string _settingsPath;
    private readonly int _ownProcessId = Environment.ProcessId;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private string? _selectedExecutable;
    private bool _autoMonitor = true;
    private int _refreshRequested;
    private bool _disposed;
    private GameFpsMonitor? _fpsMonitor;
    private bool _monitorAttached;
    private string? _attachedExecutable;
    private int _attachedProcessId;
    private string? _lastError;

    public GameMonitorService()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = AppContext.BaseDirectory;
        }

        var directory = Path.Combine(localAppData, "ThermalScope");
        _settingsPath = Path.Combine(directory, "game-monitor.json");
        LoadSettings();
    }

    public event Action<GameMonitorSnapshot>? SnapshotReady;
    public event Action<string>? MonitorError;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || (_worker is { IsCompleted: false }))
            {
                return;
            }

            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _worker = Task.Run(() => RunAsync(cancellation.Token), CancellationToken.None);
        }
    }

    public void Configure(string executableName, bool autoMonitor)
    {
        var normalized = NormalizeExecutableName(executableName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        lock (_gate)
        {
            _selectedExecutable = normalized;
            _autoMonitor = autoMonitor;
            _lastError = null;
            SaveSettings();
            Interlocked.Exchange(ref _refreshRequested, 1);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _selectedExecutable = null;
            _autoMonitor = false;
            _lastError = null;
            SaveSettings();
            Interlocked.Exchange(ref _refreshRequested, 1);
        }
    }

    public void RequestRefresh() => Interlocked.Exchange(ref _refreshRequested, 1);

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        Task? worker;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancellation = _cancellation;
            worker = _worker;
            _cancellation = null;
            _worker = null;
        }

        try
        {
            cancellation?.Cancel();
            worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Normal shutdown.
        }
        finally
        {
            cancellation?.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _fpsMonitor = new GameFpsMonitor();
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    SnapshotReady?.Invoke(Capture());
                }
                catch (Exception exception)
                {
                    MonitorError?.Invoke(exception.Message);
                }

                try
                {
                    await WaitForNextSampleAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            MonitorError?.Invoke(exception.Message);
        }
        finally
        {
            if (_fpsMonitor is not null)
            {
                var stopError = string.Empty;
                StopMonitor(_fpsMonitor, ref stopError);
            }

            _fpsMonitor?.Dispose();
            _fpsMonitor = null;
        }
    }

    private async Task WaitForNextSampleAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(1))
        {
            if (Interlocked.Exchange(ref _refreshRequested, 0) == 1)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }

        Interlocked.Exchange(ref _refreshRequested, 0);
    }

    private GameMonitorSnapshot Capture()
    {
        string? selectedExecutable;
        bool autoMonitor;
        lock (_gate)
        {
            selectedExecutable = _selectedExecutable;
            autoMonitor = _autoMonitor;
        }

        var processList = EnumerateProcesses(selectedExecutable);
        var selectedProcess = processList.FirstOrDefault(process =>
            selectedExecutable is not null &&
            process.Name.Equals(selectedExecutable, StringComparison.OrdinalIgnoreCase));

        var monitor = _fpsMonitor;
        var monitorAvailable = monitor?.IsAvailable == true;
        var monitorStatus = monitor?.Status ?? "The FPS monitor has not been initialized.";
        var error = _lastError;
        var monitorAttached = _monitorAttached;
        var status = string.Empty;

        if (string.IsNullOrWhiteSpace(selectedExecutable))
        {
            StopMonitor(monitor, ref error);
            status = "Select a game executable to monitor FPS.";
        }
        else if (selectedProcess is null)
        {
            StopMonitor(monitor, ref error);
            status = $"Waiting for {selectedExecutable} to open.";
        }
        else if (!autoMonitor)
        {
            StopMonitor(monitor, ref error);
            status = $"{selectedExecutable} is running; automatic FPS monitoring is off.";
        }
        else if (!monitorAvailable)
        {
            StopMonitor(monitor, ref error);
            status = $"{selectedExecutable} is running, but the standalone FPS monitor is unavailable.";
        }
        else
        {
            var targetChanged = !_monitorAttached ||
                !string.Equals(_attachedExecutable, selectedExecutable, StringComparison.OrdinalIgnoreCase) ||
                _attachedProcessId != selectedProcess.ProcessId;
            if (targetChanged)
            {
                StopMonitor(monitor, ref error);
                if (monitor!.TryAttach(selectedProcess.ProcessId, out var attachError))
                {
                    _attachedExecutable = selectedExecutable;
                    _attachedProcessId = selectedProcess.ProcessId;
                    _monitorAttached = true;
                    _lastError = null;
                    error = null;
                    monitorAttached = true;
                    status = $"{selectedExecutable} running · FPS monitor active.";
                }
                else
                {
                    _lastError = attachError;
                    error = attachError;
                    monitorAttached = false;
                    status = $"{selectedExecutable} is running; FPS monitoring could not attach.";
                }
            }
            else
            {
                monitorAttached = true;
                status = $"{selectedExecutable} running · FPS monitor active.";
            }
        }

        monitorAttached = _monitorAttached;
        var visibleProcesses = processList
            .Where(process => !string.IsNullOrWhiteSpace(process.WindowTitle) || process.Name.Equals(selectedExecutable, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(process => process.Name.Equals(selectedExecutable, StringComparison.OrdinalIgnoreCase))
            .ThenBy(process => process.WindowTitle.Length == 0 ? process.Name : process.WindowTitle)
            .Take(20)
            .ToList();

        return new GameMonitorSnapshot(
            DateTimeOffset.Now,
            visibleProcesses,
            selectedProcess,
            selectedExecutable,
            autoMonitor,
            monitorAvailable,
            monitorAttached,
            monitorStatus,
            status,
            error);
    }

    private List<GameProcessInfo> EnumerateProcesses(string? selectedExecutable)
    {
        var result = new List<GameProcessInfo>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return result;
        }

        foreach (var process in processes)
        {
            try
            {
                if (process.Id == _ownProcessId || process.HasExited)
                {
                    continue;
                }

                process.Refresh();
                var title = process.MainWindowTitle ?? string.Empty;
                var executableName = NormalizeExecutableName(process.ProcessName) ?? $"{process.ProcessName}.exe";
                if (string.IsNullOrWhiteSpace(title) &&
                    !executableName.Equals(selectedExecutable, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(new GameProcessInfo(
                    process.Id,
                    executableName,
                    title,
                    Math.Max(0, process.WorkingSet64),
                    _fpsMonitor?.ReadFps(process.Id)));
            }
            catch
            {
                // Processes can exit or deny access while the list is being enumerated.
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }

    private void StopMonitor(GameFpsMonitor? monitor, ref string? error)
    {
        if (!_monitorAttached || monitor is null)
        {
            return;
        }

        if (monitor.Disable())
        {
            _monitorAttached = false;
            _attachedExecutable = null;
            _attachedProcessId = 0;
            _lastError = null;
            error = null;
        }
        else
        {
            _lastError = "The FPS monitor could not be stopped cleanly.";
            error = _lastError;
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return;
            }

            var settings = JsonSerializer.Deserialize<GameMonitorSettings>(File.ReadAllText(_settingsPath));
            if (settings is null)
            {
                return;
            }

            _selectedExecutable = NormalizeExecutableName(settings.ExecutableName ?? string.Empty);
            _autoMonitor = settings.AutoMonitor;
        }
        catch
        {
            // Invalid settings should not prevent the monitor from starting.
        }
    }

    private void SaveSettings()
    {
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var settings = new GameMonitorSettings
            {
                ExecutableName = _selectedExecutable,
                AutoMonitor = _autoMonitor
            };
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings));
        }
        catch
        {
            // Settings persistence is best effort.
        }
    }

    private static string? NormalizeExecutableName(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        var name = Path.GetFileName(executableName.Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name}.exe";
    }

    private sealed class GameMonitorSettings
    {
        public string? ExecutableName { get; set; }
        public bool AutoMonitor { get; set; } = true;
    }
}
