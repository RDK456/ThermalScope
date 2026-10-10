using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
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

    private static Computer CreateComputer() => new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = false,
        IsMotherboardEnabled = false, // board/SuperIO temperatures are never shown as CPU or GPU values
        IsControllerEnabled = false,
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
        // Prefer the discrete card: NVIDIA, then AMD, then Intel; the one with the most memory wins a tie.
        var gpuHardware = hardware
            .Where(IsGpuHardware)
            .OrderBy(item => item.HardwareType switch
            {
                HardwareType.GpuNvidia => 0,
                HardwareType.GpuAmd => 1,
                _ => 2
            })
            .ThenByDescending(GpuMemoryMegabytes)
            .FirstOrDefault();
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

        // Windows' own counters fill clock and power when the driver-based sensors are silent.
        cpu = cpu with
        {
            ClockMegahertz = cpu.ClockMegahertz ?? usage.CpuClockMegahertz,
            PowerWatts = cpu.PowerWatts ?? usage.CpuPowerWatts
        };

        // Real CPU die readings published by MSI Afterburner, used only for what LibreHardwareMonitor could not
        // read (no PawnIO driver). Board/ACPI thermal zones are never substituted.
        var external = AfterburnerReader.TryRead();
        var externalReadings = new List<SensorReading>();
        if (external is not null)
        {
            if (!cpu.TemperatureCelsius.HasValue && external.Temperature is { } externalTemp)
            {
                cpu = cpu with { TemperatureCelsius = externalTemp, TemperatureLabel = "CPU die · MSI Afterburner" };
            }

            if (!cpu.ClockMegahertz.HasValue && external.ClockMegahertz is { } externalClock)
            {
                cpu = cpu with { ClockMegahertz = externalClock };
            }

            if (!cpu.PowerWatts.HasValue && external.PowerWatts is { } externalPower)
            {
                cpu = cpu with { PowerWatts = externalPower };
            }

            if (!cpuSensors.Any(sensor => sensor.SensorType == SensorType.Temperature && ReadSensorValue(sensor).HasValue))
            {
                if (external.Temperature is { } package)
                {
                    externalReadings.Add(new SensorReading("CPU", "CPU die (MSI Afterburner)", nameof(SensorType.Temperature), package, "°C"));
                }

                foreach (var (name, value) in external.ThreadTemperatures)
                {
                    externalReadings.Add(new SensorReading("CPU", $"{name.Replace(" temperature", string.Empty)} (MSI Afterburner)", nameof(SensorType.Temperature), value, "°C"));
                }
            }
        }

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
            .Concat(externalReadings)
            .OrderBy(reading => reading.Component == "CPU" ? 0 : 1)
            .ThenBy(reading => reading.Value.HasValue ? 0 : 1) // working sensors first, silent ones last
            .ThenByDescending(reading => reading.Category == nameof(SensorType.Temperature))
            .ThenBy(reading => reading.Category)
            .ThenBy(reading => NaturalKey(reading.Name), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var noticeParts = new[] { startupNotice, updateNotice }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
        var source = computer is null ? "Windows performance counters" : "LibreHardwareMonitor";
        if (externalReadings.Count > 0 || (external is not null && cpu.TemperatureLabel.Contains("Afterburner")))
        {
            source += " + MSI Afterburner";
        }
        var notice = noticeParts.Length == 0 ? null : string.Join("  ", noticeParts);

        // Only CPU die/package sensors are ever used for the CPU value. Windows exposes no driver-free
        // equivalent: ACPI thermal zones are motherboard/ambient readings and are deliberately ignored.
        var cpuSensorsNeedDriver = cpuHardware is not null && !cpu.TemperatureCelsius.HasValue;

        if (cpuHardware is null || gpuHardware is null)
        {
            notice = BuildNotice(notice, "Some CPU or GPU hardware was not reported by the sensor backend.");
        }
        else if (!cpuSensorsNeedDriver && !gpu.TemperatureCelsius.HasValue)
        {
            notice = BuildNotice(notice, "No GPU temperature sensor was reported by the graphics driver.");
        }

        return new MonitorSnapshot(
            DateTimeOffset.Now,
            cpu,
            gpu,
            readings,
            source,
            notice,
            cpuSensorsNeedDriver);
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

    // "Core #2" sorts before "Core #10".
    private static string NaturalKey(string name) =>
        Regex.Replace(name, @"\d+", match => match.Value.PadLeft(6, '0'));

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
            : new[] { "cpu package", "tctl", "tdie", "package", "core max", "core average", "average", "core" };

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

        if (!isGpu)
        {
            // Average of the per-core clocks (the bus speed sensor is not a core clock).
            var cores = clocks
                .Where(sensor => sensor.Name.Contains("core", StringComparison.OrdinalIgnoreCase) &&
                                 !sensor.Name.Contains("effective", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (cores.Count > 0)
            {
                return cores.Average(sensor => ReadSensorValue(sensor) ?? 0);
            }
        }

        var selected = FindByName(
            clocks,
            isGpu
                ? new[] { "gpu core", "core", "graphics" }
                : new[] { "cpu core", "core", "cpu" });
        selected ??= clocks[0];
        return ReadSensorValue(selected); // megahertz; the UI picks MHz or GHz
    }

    private static double GpuMemoryMegabytes(IHardware hardware) =>
        FlattenHardware(hardware)
            .SelectMany(item => item.Sensors)
            .Where(sensor => sensor.SensorType == SensorType.SmallData &&
                             sensor.Name.Contains("memory total", StringComparison.OrdinalIgnoreCase))
            .Select(sensor => ReadSensorValue(sensor) ?? 0)
            .DefaultIfEmpty(0)
            .Max();

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
        var watts = ReadSensorValue(selected);
        // Without ring-0 access the CPU package power reads a constant 0, which is "no data", not 0 W.
        return !isGpu && watts is <= 0 ? null : watts;
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

    private readonly record struct UsageSample(
        double? CpuPercent,
        double? GpuPercent,
        double? CpuClockMegahertz = null,
        double? CpuPowerWatts = null);

    private sealed class WindowsUsageProvider : IDisposable
    {
        private PerformanceCounter? _cpuCounter;
        private PerformanceCounter? _performanceCounter; // % of base frequency (above 100 = turbo)
        private PerformanceCounter? _frequencyCounter;   // base frequency in MHz
        private PerformanceCounter? _packagePowerCounter; // RAPL package power in milliwatts, when the platform exposes it
        private double? _lastGpuPercent;
        private int _sampleCount;

        public WindowsUsageProvider()
        {
            // Driver-free CPU clock and package power straight from Windows (what Task Manager uses).
            _performanceCounter = TryCounter("Processor Information", "% Processor Performance", "_Total");
            _frequencyCounter = TryCounter("Processor Information", "Processor Frequency", "_Total");
            _packagePowerCounter = TryCounter("Energy Meter", "Power", "RAPL_Package0_PKG");

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

            double? clock = null;
            double? power = null;
            try
            {
                if (_performanceCounter is not null && _frequencyCounter is not null)
                {
                    var mhz = _performanceCounter.NextValue() / 100.0 * _frequencyCounter.NextValue();
                    clock = mhz > 0 && double.IsFinite(mhz) ? mhz : null;
                }

                if (_packagePowerCounter is not null)
                {
                    var watts = _packagePowerCounter.NextValue() / 1000.0;
                    power = watts > 0 && double.IsFinite(watts) ? watts : null;
                }
            }
            catch
            {
                // Counters can disappear during sleep/wake; the next sample simply has no value.
            }

            return new UsageSample(cpu, _lastGpuPercent, clock, power);
        }

        private static PerformanceCounter? TryCounter(string category, string counter, string instance)
        {
            try
            {
                var created = new PerformanceCounter(category, counter, instance, true);
                created.NextValue(); // the first reading of a rate counter is always 0
                return created;
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            _cpuCounter?.Dispose();
            _performanceCounter?.Dispose();
            _frequencyCounter?.Dispose();
            _packagePowerCounter?.Dispose();
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
