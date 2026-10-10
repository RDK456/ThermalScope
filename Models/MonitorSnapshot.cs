namespace ThermalScope.Models;

public sealed record MetricSnapshot(
    string Name,
    double? UsagePercent,
    double? TemperatureCelsius,
    string TemperatureLabel,
    double? ClockMegahertz,
    double? PowerWatts);

public sealed record SensorReading(
    string Component,
    string Name,
    string Category,
    double? Value,
    string Unit);

public sealed record MonitorSnapshot(
    DateTimeOffset Timestamp,
    MetricSnapshot Cpu,
    MetricSnapshot Gpu,
    IReadOnlyList<SensorReading> Sensors,
    string Source,
    string? Notice = null,
    bool CpuSensorsNeedDriver = false);
