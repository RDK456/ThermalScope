using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ThermalScope.Models;

namespace ThermalScope.Services;

/// <summary>Finds the game, attaches the frame pacer helper, applies per-game limits and publishes live stats.</summary>
public sealed class GameMonitorService : IDisposable
{
    public const int MinFps = 20;
    public const int MaxFps = 500;
    private const int TickMilliseconds = 250;
    private const int ScanEveryTicks = 8;            // process list refresh, ~2 s
    private const int GraphFrames = 240;
    private const int HookTimeoutSeconds = 15;

    private readonly object _gate = new();
    private readonly string _settingsPath;
    private readonly int _ownProcessId = Environment.ProcessId;
    private readonly GameMonitorSettings _settings;
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private int _refreshRequested;
    private bool _disposed;
    private GameFpsMonitor? _fpsMonitor;
    private List<GameProcessInfo> _processes = new();
    private int _ticksSinceScan = ScanEveryTicks;
    private DetectedGame? _detected;
    private bool _monitorAttached;
    private int _attachedProcessId;
    private DateTime _attachedAtUtc;
    private string? _lastError;
    private string? _currentExecutable;
    private bool _limiterOn = true;

    public GameMonitorService()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = AppContext.BaseDirectory;
        }

        _settingsPath = Path.Combine(localAppData, "ThermalScope", "game-monitor.json");
        _settings = LoadSettings();
    }

    public event Action<GameMonitorSnapshot>? SnapshotReady;
    public event Action<string>? MonitorError;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _worker is { IsCompleted: false })
            {
                return;
            }

            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _worker = Task.Run(() => RunAsync(cancellation.Token), CancellationToken.None);
        }
    }

    /// <summary>Manual override: always monitor this executable instead of the auto-detected game.</summary>
    public void Configure(string executableName, bool autoMonitor)
    {
        var normalized = NormalizeExecutableName(executableName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        lock (_gate)
        {
            _settings.ExecutableName = normalized;
            _settings.AutoMonitor = autoMonitor;
            _lastError = null;
            SaveSettings();
            Refresh();
        }
    }

    /// <summary>Drops the manual override and goes back to detecting the game automatically.</summary>
    public void UseAutoDetect()
    {
        lock (_gate)
        {
            _settings.ExecutableName = null;
            _settings.AutoDetect = true;
            _detected = null;
            _lastError = null;
            SaveSettings();
            Refresh();
        }
    }

    public void SetAutoMonitor(bool enabled)
    {
        lock (_gate)
        {
            _settings.AutoMonitor = enabled;
            SaveSettings();
            Refresh();
        }
    }

    /// <summary>Stores the limit for the current game (or as the default for new games) and applies it live.</summary>
    public void SetLimit(int fps)
    {
        lock (_gate)
        {
            var value = fps <= 0 ? 0 : Math.Clamp(fps, MinFps, MaxFps);
            _settings.DefaultFps = value;
            if (_currentExecutable is not null)
            {
                _settings.Limits[_currentExecutable] = value;
            }

            SaveSettings();
            Refresh();
        }
    }

    /// <summary>Hotkey helper: change the limit by a step; starts from 60 when it was unlimited.</summary>
    public void NudgeLimit(int delta)
    {
        int current;
        lock (_gate)
        {
            current = EffectiveStoredLimit(_currentExecutable);
        }

        SetLimit(current <= 0 ? 60 : Math.Clamp(current + delta, MinFps, MaxFps));
    }

    /// <summary>Console-style pacing: let the display refresh set the pace when the target divides it.</summary>
    public void SetPacingLocked(bool locked)
    {
        lock (_gate)
        {
            _settings.PacingLocked = locked;
            SaveSettings();
            Refresh();
        }
    }

    public bool PacingLocked
    {
        get
        {
            lock (_gate)
            {
                return _settings.PacingLocked;
            }
        }
    }

    public void SetLimiterEnabled(bool enabled)
    {
        lock (_gate)
        {
            _limiterOn = enabled;
            Refresh();
        }
    }

    public bool LimiterEnabled
    {
        get
        {
            lock (_gate)
            {
                return _limiterOn;
            }
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

    private void Refresh()
    {
        _ticksSinceScan = ScanEveryTicks;
        Interlocked.Exchange(ref _refreshRequested, 1);
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
                    await WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
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
            // Releases the limiter inside the game before the process goes away.
            _fpsMonitor?.Dispose();
            _fpsMonitor = null;
        }
    }

    private async Task WaitForNextTickAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < TickMilliseconds)
        {
            if (Interlocked.Exchange(ref _refreshRequested, 0) == 1)
            {
                return;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private GameMonitorSnapshot Capture()
    {
        string? manualExecutable;
        bool autoDetect, autoMonitor, limiterOn, pacingLocked;
        lock (_gate)
        {
            manualExecutable = _settings.ExecutableName;
            autoDetect = _settings.AutoDetect;
            autoMonitor = _settings.AutoMonitor;
            limiterOn = _limiterOn;
            pacingLocked = _settings.PacingLocked;
        }

        if (++_ticksSinceScan >= ScanEveryTicks)
        {
            _processes = EnumerateProcesses(manualExecutable);
            _ticksSinceScan = 0;
        }

        // Which game are we dealing with?
        GameProcessInfo? target = null;
        var executable = manualExecutable;
        var autoDetected = false;
        if (executable is not null)
        {
            target = _processes.FirstOrDefault(p => p.Name.Equals(executable, StringComparison.OrdinalIgnoreCase));
        }
        else if (autoDetect)
        {
            RefreshDetectedGame();
            if (_detected is { } detected)
            {
                executable = detected.ExecutableName;
                autoDetected = true;
                target = _processes.FirstOrDefault(p => p.ProcessId == detected.ProcessId)
                    ?? new GameProcessInfo(detected.ProcessId, detected.ExecutableName, detected.WindowTitle, 0, null);
            }
        }

        int storedLimit;
        lock (_gate)
        {
            _currentExecutable = executable;
            storedLimit = EffectiveStoredLimit(executable);
        }

        var monitor = _fpsMonitor;
        var available = monitor?.IsAvailable == true;
        var consent = AppSettings.Current.FpsConsentAccepted;
        var consentRequired = false;
        string status;
        string? error = _lastError;

        if (executable is null)
        {
            StopMonitor(monitor);
            status = autoDetect
                ? "Waiting for a game. Start one in borderless fullscreen and it is picked up automatically."
                : "Pick a game to monitor.";
        }
        else if (target is null)
        {
            StopMonitor(monitor);
            status = $"Waiting for {executable} to open.";
        }
        else if (!autoMonitor)
        {
            StopMonitor(monitor);
            status = $"{executable} is running. Automatic pacing is off.";
        }
        else if (!available)
        {
            StopMonitor(monitor);
            status = $"{executable} is running, but the frame pacer is unavailable.";
            error ??= monitor?.Status;
        }
        else if (!consent)
        {
            StopMonitor(monitor);
            consentRequired = true;
            status = $"{executable} found. Allow ThermalScope to attach to it to start limiting.";
        }
        else
        {
            if (!_monitorAttached || _attachedProcessId != target.ProcessId)
            {
                StopMonitor(monitor);
                if (monitor!.TryAttach(target.ProcessId, out var attachError))
                {
                    _monitorAttached = true;
                    _attachedProcessId = target.ProcessId;
                    _attachedAtUtc = DateTime.UtcNow;
                    _lastError = error = null;
                }
                else
                {
                    _lastError = error = attachError;
                }
            }

            if (_monitorAttached)
            {
                monitor!.SetPacingLocked(pacingLocked);
                monitor.SetTargetFps(limiterOn ? storedLimit : 0);
                if (monitor.IsHookReady)
                {
                    status = limiterOn && storedLimit > 0
                        ? $"Limiting {executable} to {storedLimit} FPS."
                        : $"Watching {executable}. No frame limit is set.";
                    _lastError = error = null;
                }
                else if ((DateTime.UtcNow - _attachedAtUtc).TotalSeconds > HookTimeoutSeconds)
                {
                    status = $"Attached to {executable}, but no DirectX 10/11/12 swap chain was found.";
                    error = "Vulkan, OpenGL and DirectX 8 are not supported.";
                }
                else
                {
                    status = $"Attached to {executable}. Waiting for it to start rendering…";
                }
            }
            else
            {
                status = $"{executable} is running, but the frame pacer could not attach.";
            }
        }

        FpsSample? sample = null;
        var frameTimes = Array.Empty<float>();
        if (_monitorAttached && target is not null && monitor is not null)
        {
            sample = monitor.ReadFps(target.ProcessId);
            frameTimes = monitor.ReadFrameTimes(target.ProcessId, GraphFrames);
            target = target with { Fps = sample };
        }

        return new GameMonitorSnapshot(
            DateTimeOffset.Now,
            _processes,
            target,
            executable,
            autoDetected,
            autoMonitor,
            available,
            _monitorAttached,
            _monitorAttached && monitor?.IsHookReady == true,
            consentRequired,
            storedLimit,
            limiterOn,
            frameTimes,
            status,
            error,
            _monitorAttached ? monitor?.RefreshHz ?? 0 : 0,
            pacingLocked);
    }

    private void RefreshDetectedGame()
    {
        // Sticky: keep the last game while it is alive, even when ThermalScope itself has the focus.
        if (_detected is { } current && IsAlive(current.ProcessId))
        {
            var foreground = GameDetector.FindForegroundGame(_ownProcessId);
            if (foreground is not null && foreground.ProcessId != current.ProcessId)
            {
                _detected = foreground;
            }

            return;
        }

        _detected = GameDetector.FindForegroundGame(_ownProcessId);
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private int EffectiveStoredLimit(string? executable) =>
        executable is not null && _settings.Limits.TryGetValue(executable, out var limit)
            ? limit
            : _settings.DefaultFps;

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

                var title = process.MainWindowTitle ?? string.Empty;
                var executableName = NormalizeExecutableName(process.ProcessName) ?? $"{process.ProcessName}.exe";
                var isSelected = executableName.Equals(selectedExecutable, StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrWhiteSpace(title) && !isSelected)
                {
                    continue;
                }

                result.Add(new GameProcessInfo(process.Id, executableName, title, Math.Max(0, process.WorkingSet64), null));
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

        return result
            .OrderBy(p => p.WindowTitle.Length == 0 ? 1 : 0)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(60)
            .ToList();
    }

    private void StopMonitor(GameFpsMonitor? monitor)
    {
        if (!_monitorAttached || monitor is null)
        {
            return;
        }

        monitor.Disable();
        _monitorAttached = false;
        _attachedProcessId = 0;
    }

    private GameMonitorSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var loaded = JsonSerializer.Deserialize<GameMonitorSettings>(File.ReadAllText(_settingsPath));
                if (loaded is not null)
                {
                    loaded.ExecutableName = NormalizeExecutableName(loaded.ExecutableName ?? string.Empty);
                    loaded.Limits = new Dictionary<string, int>(loaded.Limits, StringComparer.OrdinalIgnoreCase);
                    return loaded;
                }
            }
        }
        catch
        {
            // Invalid settings should not prevent the monitor from starting.
        }

        return new GameMonitorSettings();
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
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

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.exe";
    }

    private sealed class GameMonitorSettings
    {
        public string? ExecutableName { get; set; }
        public bool AutoMonitor { get; set; } = true;
        public bool AutoDetect { get; set; } = true;
        public int DefaultFps { get; set; }
        public bool PacingLocked { get; set; }
        public Dictionary<string, int> Limits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
