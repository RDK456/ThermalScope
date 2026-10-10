namespace ThermalScope.Models;

/// <summary>Frame statistics published by the in-game helper every half second.</summary>
public readonly record struct FpsSample(double Average, double Low, double FrameTimeMs);

public sealed record GameProcessInfo(
    int ProcessId,
    string Name,
    string WindowTitle,
    long WorkingSetBytes,
    FpsSample? Fps);

public sealed record GameMonitorSnapshot(
    DateTimeOffset Timestamp,
    IReadOnlyList<GameProcessInfo> VisibleProcesses,
    GameProcessInfo? SelectedProcess,
    string? SelectedExecutable,
    bool AutoDetected,
    bool AutoMonitor,
    bool FpsMonitorAvailable,
    bool MonitorAttached,
    bool MonitorReady,
    bool ConsentRequired,
    int TargetFps,
    bool LimiterEnabled,
    float[] FrameTimes,
    string Status,
    string? Error = null,
    int RefreshHz = 0,
    bool PacingLocked = false);
