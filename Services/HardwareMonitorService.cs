using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.PawnIo;
using ThermalScope.Models;

namespace ThermalScope.Services;

public sealed class HardwareMonitorService : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private int _intervalSeconds = 2;
    private int _refreshRequested;
    private bool _disposed;

    public event Action<MonitorSnapshot>? SnapshotReady;
    public event Action<string>? MonitorError;

    public void Start(int intervalSeconds)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _intervalSeconds = NormalizeInterval(intervalSeconds);
            Interlocked.Exchange(ref _refreshRequested, 0);
            if (_worker is { IsCompleted: false })
            {
                return;
            }

            _cancellation?.Dispose();
            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _worker = Task.Run(() => RunAsync(cancellation.Token), CancellationToken.None);
        }
    }

    public void SetInterval(int intervalSeconds)
    {
        lock (_gate)
        {
            _intervalSeconds = NormalizeInterval(intervalSeconds);
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
            // The worker owns the sensor handles and is shutting down.
        }
        finally
        {
            cancellation?.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        Computer? computer = null;
        WindowsUsageProvider? usageProvider = null;
        string? startupNotice = null;

        try
        {
            try
            {
                usageProvider = new WindowsUsageProvider();
            }
            catch (Exception exception)
            {
                startupNotice = $"Windows performance counters are unavailable: {exception.Message}";
            }

            var pawnIoNotice = GetPawnIoNotice();
            if (pawnIoNotice is not null)
            {
                startupNotice = BuildNotice(startupNotice, pawnIoNotice);
            }

            try
            {
                computer = CreateComputer();
                computer.Open();
            }
            catch (Exception exception)
            {
                startupNotice = BuildNotice(startupNotice, $"Hardware sensor access was not available: {exception.Message}");
                MonitorError?.Invoke(exception.Message);
                try
                {
                    computer?.Close();
                }
                catch
                {
                    // Continue with the Windows counter fallback.
                }
                computer = null;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var snapshot = Capture(computer, usageProvider, startupNotice);
                    SnapshotReady?.Invoke(snapshot);
                }
                catch (Exception exception)
                {
                    MonitorError?.Invoke(exception.Message);
                }

                var delay = TimeSpan.FromSeconds(GetIntervalSeconds());
                try
                {
                    await WaitForNextSampleAsync(delay, cancellationToken).ConfigureAwait(false);
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
            try
            {
                computer?.Close();
            }
            catch
            {
                // Some sensor drivers can fail while the machine is shutting down.
            }

            usageProvider?.Dispose();
        }
    }

    private async Task WaitForNextSampleAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < delay)
        {
            if (Interlocked.Exchange(ref _refreshRequested, 0) == 1)
            {
                return;
            }

            var remaining = delay - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var slice = remaining < TimeSpan.FromMilliseconds(200)
                ? remaining
                : TimeSpan.FromMilliseconds(200);
            await Task.Delay(slice, cancellationToken).ConfigureAwait(false);
        }

        Interlocked.Exchange(ref _refreshRequested, 0);
    }

    private static string? GetPawnIoNotice()
    {
        try
        {
            return PawnIo.IsInstalled
                ? null
                : "PawnIO is not installed; some hardware temperature sensors may be unavailable.";
        }
        catch (Exception exception)
        {
            return $"PawnIO status could not be determined: {exception.Message}";
        }
    }

    private static Computer CreateComputer() => new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = false,
        IsMotherboardEnabled = true,
        IsControllerEnabled = true,
        IsNetworkEnabled = false,
        IsStorageEnabled = false
    };

    private static MonitorSnapshot Capture(
        Computer? computer,
        WindowsUsageProvider? usageProvider,
        string? startupNotice)
    {
        string? updateNotice = null;
        if (computer is not null)
        {
            try
            {
                computer.Accept(new UpdateVisitor());
            }
            catch (Exception exception)
            {
                updateNotice = $"A sensor update failed: {exception.Message}";
            }
        }

        var usage = usageProvider?.Sample() ?? new UsageSample(null, null);
        var hardware = computer?.Hardware
            .Where(item => item.HardwareType == HardwareType.Cpu || IsGpuHardware(item))
            .ToList() ?? new List<IHardware>();
        var cpuHardware = hardware.FirstOrDefault(item => item.HardwareType == HardwareType.Cpu);
        var gpuHardware = hardware.FirstOrDefault(IsGpuHardware);
        var cpuSensors = GetSensors(cpuHardware);
        var gpuSensors = GetSensors(gpuHardware);

        var cpu = CreateMetric(
            cpuHardware,
            cpuSensors,
            isGpu: false,
            fallbackUsage: usage.CpuPercent);
        var gpu = CreateMetric(
            gpuHardware,
            gpuSensors,
            isGpu: true,
            fallbackUsage: usage.GpuPercent);

        var readings = hardware
            .SelectMany(FlattenHardware)
            .SelectMany(item => item.Sensors)
            .Where(IsInterestingSensor)
            .Select(sensor => new SensorReading(
                GetComponentName(sensor.Hardware),
                sensor.Name,
                sensor.SensorType.ToString(),
                ReadSensorValue(sensor),
                GetUnit(sensor.SensorType)))
            .OrderBy(reading => reading.Component == "CPU" ? 0 : 1)
            .ThenByDescending(reading => reading.Category == nameof(SensorType.Temperature))
            .ThenBy(reading => reading.Value.HasValue ? 0 : 1)
            .ThenBy(reading => reading.Category)
            .ThenBy(reading => reading.Name)
            .ToList();

        var noticeParts = new[] { startupNotice, updateNotice }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
        var source = computer is null ? "Windows performance counters" : "LibreHardwareMonitor";
        var notice = noticeParts.Length == 0 ? null : string.Join("  ", noticeParts);

        if (cpuHardware is null || gpuHardware is null)
        {
            notice = BuildNotice(notice, "Some CPU or GPU hardware was not reported by the sensor backend.");
        }
        else if (!cpu.TemperatureCelsius.HasValue && !gpu.TemperatureCelsius.HasValue)
        {
            notice = BuildNotice(notice, "No CPU/GPU temperature sensors were reported. Run as administrator if your hardware requires it.");
        }

        return new MonitorSnapshot(
            DateTimeOffset.Now,
            cpu,
            gpu,
            readings,
            source,
            notice);
    }

    private static MetricSnapshot CreateMetric(
        IHardware? hardware,
        IReadOnlyCollection<ISensor> sensors,
        bool isGpu,
        double? fallbackUsage)
    {
        var (temperature, temperatureLabel) = SelectTemperature(sensors, isGpu);
        var clock = SelectClock(sensors, isGpu);
        var power = SelectPower(sensors, isGpu);
        var usage = SelectUsage(sensors, isGpu) ?? fallbackUsage;

        return new MetricSnapshot(
            hardware?.Name ?? (isGpu ? "Graphics adapter" : "Processor"),
            usage,
            temperature,
            temperatureLabel,
            clock,
            power);
    }

    private static bool IsGpuHardware(IHardware hardware) =>
        hardware.HardwareType.ToString().StartsWith("Gpu", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyCollection<ISensor> GetSensors(IHardware? hardware)
    {
        if (hardware is null)
        {
            return Array.Empty<ISensor>();
        }

        return FlattenHardware(hardware)
            .SelectMany(item => item.Sensors)
            .Where(IsInterestingSensor)
            .ToArray();
    }

    private static IEnumerable<IHardware> FlattenHardware(IHardware? hardware)
    {
        if (hardware is null)
        {
            yield break;
        }

        yield return hardware;

        foreach (var child in hardware.SubHardware)
        {
            foreach (var descendant in FlattenHardware(child))
            {
                yield return descendant;
            }
        }
    }

    private static bool IsInterestingSensor(ISensor sensor) => sensor.SensorType is
        SensorType.Temperature or
        SensorType.Load or
        SensorType.Clock or
        SensorType.Power;

    private static double? ReadSensorValue(ISensor sensor)
    {
        if (!sensor.Value.HasValue)
        {
            return null;
        }

        var value = sensor.Value.Value;
        return float.IsFinite(value) ? value : null;
    }

    private static string GetComponentName(IHardware? hardware)
    {
        if (hardware is null)
        {
            return "Unknown";
        }

        return hardware.HardwareType == HardwareType.Cpu ? "CPU" : "GPU";
    }

    private static string GetUnit(SensorType sensorType) => sensorType switch
    {
        SensorType.Temperature => "°C",
        SensorType.Load => "%",
        SensorType.Clock => "MHz",
        SensorType.Power => "W",
        _ => string.Empty
    };

    private static (double? Value, string Label) SelectTemperature(
        IEnumerable<ISensor> sensors,
        bool isGpu)
    {
        var allCandidates = sensors.Where(sensor => sensor.SensorType == SensorType.Temperature).ToList();
        if (allCandidates.Count == 0)
        {
            return (null, "Not available");
        }

        var candidates = allCandidates.Where(sensor => !IsAuxiliaryTemperature(sensor.Name)).ToList();
        if (candidates.Count == 0)
        {
            candidates = allCandidates;
        }

        var availableCandidates = candidates
            .Where(sensor => ReadSensorValue(sensor).HasValue)
            .ToList();
        var preferredNames = isGpu
            ? new[] { "gpu core", "core", "temperature", "gpu" }
            : new[] { "tctl", "tdie", "package", "core average", "average", "core" };

        var selected = FindByName(availableCandidates, preferredNames);
        selected ??= availableCandidates.FirstOrDefault();
        selected ??= FindByName(candidates, preferredNames);
        selected ??= candidates[0];
        return (ReadSensorValue(selected), selected.Name);
    }

    private static double? SelectUsage(IEnumerable<ISensor> sensors, bool isGpu)
    {
        var loads = sensors
            .Where(sensor => sensor.SensorType == SensorType.Load && ReadSensorValue(sensor).HasValue)
            .ToList();
        if (loads.Count == 0)
        {
            return null;
        }

        if (isGpu)
        {
            var gpuLoads = loads
                .Where(sensor => !sensor.Name.Contains("memory", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (gpuLoads.Count == 0)
            {
                gpuLoads = loads;
            }

            var preferred = FindByName(gpuLoads, new[] { "gpu core", "3d", "core", "total" });
            if (preferred is not null)
            {
                return ClampPercent(ReadSensorValue(preferred) ?? 0);
            }

            return ClampPercent(gpuLoads.Max(sensor => ReadSensorValue(sensor) ?? 0));
        }

        var total = FindByName(loads, new[] { "cpu total", "total", "package" });
        if (total is not null)
        {
            return ClampPercent(ReadSensorValue(total) ?? 0);
        }

        var coreLoads = loads
            .Where(sensor => sensor.Name.Contains("core", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (coreLoads.Count > 0)
        {
            return ClampPercent(coreLoads.Average(sensor => ReadSensorValue(sensor) ?? 0));
        }

        return ClampPercent(loads.Average(sensor => ReadSensorValue(sensor) ?? 0));
    }

    private static double? SelectClock(IEnumerable<ISensor> sensors, bool isGpu)
    {
        var clocks = sensors
            .Where(sensor => sensor.SensorType == SensorType.Clock && ReadSensorValue(sensor).HasValue)
            .ToList();
        if (clocks.Count == 0)
        {
            return null;
        }

        var selected = FindByName(
            clocks,
            isGpu
                ? new[] { "gpu core", "core", "graphics" }
                : new[] { "cpu core", "core", "cpu" });
        selected ??= clocks[0];
        var value = ReadSensorValue(selected) ?? 0;
        return value > 1000 ? value / 1000 : value;
    }

    private static double? SelectPower(IEnumerable<ISensor> sensors, bool isGpu)
    {
        var powerSensors = sensors
            .Where(sensor => sensor.SensorType == SensorType.Power && ReadSensorValue(sensor).HasValue)
            .ToList();
        if (powerSensors.Count == 0)
        {
            return null;
        }

        var selected = FindByName(
            powerSensors,
            isGpu
                ? new[] { "gpu power", "power", "total" }
                : new[] { "cpu package", "package", "cpu total", "total" });
        selected ??= powerSensors[0];
        return ReadSensorValue(selected);
    }

    private static bool IsAuxiliaryTemperature(string name) =>
        name.Contains("memory", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("junction", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("hot spot", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("hotspot", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("vrm", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("board", StringComparison.OrdinalIgnoreCase);

    private static ISensor? FindByName(IEnumerable<ISensor> sensors, IEnumerable<string> preferredNames)
    {
        foreach (var preferredName in preferredNames)
        {
            var match = sensors.FirstOrDefault(sensor =>
                sensor.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static double ClampPercent(double value) => Math.Clamp(value, 0, 100);

    private static int NormalizeInterval(int intervalSeconds) => Math.Clamp(intervalSeconds, 1, 30);

    private int GetIntervalSeconds()
    {
        lock (_gate)
        {
            return _intervalSeconds;
        }
    }

    private static string BuildNotice(string? current, string addition) =>
        string.IsNullOrWhiteSpace(current) ? addition : $"{current} {addition}";

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var child in hardware.SubHardware)
            {
                child.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor)
        {
        }

        public void VisitParameter(IParameter parameter)
        {
        }
    }

    private readonly record struct UsageSample(double? CpuPercent, double? GpuPercent);

    private sealed class WindowsUsageProvider : IDisposable
    {
        private PerformanceCounter? _cpuCounter;
        private double? _lastGpuPercent;
        private int _sampleCount;

        public WindowsUsageProvider()
        {
            try
            {
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                _cpuCounter.NextValue();
            }
            catch
            {
                _cpuCounter?.Dispose();
                _cpuCounter = null;
            }
        }

        public UsageSample Sample()
        {
            double? cpu = null;
            try
            {
                if (_cpuCounter is not null)
                {
                    cpu = ClampPercent(_cpuCounter.NextValue());
                }
            }
            catch
            {
                // The counter can disappear during driver updates or sleep/wake transitions.
            }

            if (_sampleCount++ % 4 == 0)
            {
                _lastGpuPercent = QueryGpuUsage();
            }

            return new UsageSample(cpu, _lastGpuPercent);
        }

        public void Dispose()
        {
            _cpuCounter?.Dispose();
        }

        private static double? QueryGpuUsage()
        {
            try
            {
                var scope = new ManagementScope(@"\\.\root\cimv2");
                scope.Connect();
                using var searcher = new ManagementObjectSearcher(
                    scope,
                    new ObjectQuery("SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"));
                using var results = searcher.Get();
                var byAdapter = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                foreach (ManagementObject item in results)
                {
                    var value = ToDouble(item["UtilizationPercentage"]);
                    if (!value.HasValue)
                    {
                        continue;
                    }

                    var name = item["Name"]?.ToString() ?? string.Empty;
                    var match = Regex.Match(name, @"_phys_(\d+)_", RegexOptions.IgnoreCase);
                    var adapterKey = match.Success ? match.Groups[1].Value : "0";
                    byAdapter.TryGetValue(adapterKey, out var current);
                    byAdapter[adapterKey] = current + value.Value;
                }

                return byAdapter.Count == 0
                    ? null
                    : ClampPercent(byAdapter.Values.Max());
            }
            catch
            {
                return null;
            }
        }

        private static double? ToDouble(object? value)
        {
            if (value is null)
            {
                return null;
            }

            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }
    }
}
