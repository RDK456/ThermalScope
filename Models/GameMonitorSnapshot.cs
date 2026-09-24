namespace ThermalScope.Models;

public sealed record GameProcessInfo(
    int ProcessId,
    string Name,
    string WindowTitle,
    long WorkingSetBytes,
    double? Fps);

public sealed record GameMonitorSnapshot(
    DateTimeOffset Timestamp,
    IReadOnlyList<GameProcessInfo> VisibleProcesses,
    GameProcessInfo? SelectedProcess,
    string? SelectedExecutable,
    bool AutoMonitor,
    bool FpsMonitorAvailable,
    bool MonitorAttached,
    string MonitorStatus,
    string Status,
    string? Error = null);
