using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using ThermalScope.Models;

namespace ThermalScope.Services;

/// <summary>
/// FPS for games the frame pacer must not touch (anti-cheat): reads the Present events Windows itself
/// emits (DXGI and D3D9 providers, the same source PresentMon uses). Nothing is loaded into the game.
/// Needs administrator rights to start the trace session.
/// </summary>
public sealed class EtwFpsMonitor : IDisposable
{
    private const string SessionName = "ThermalScopeFps";
    private const int RingSize = 240;
    private const double PublishWindowMs = 500;
    private const double StaleMs = 1000;
    private const int DxgiPresentStart = 42;
    private const int DxgiPresentMultiplaneStart = 45;
    private const int D3D9PresentStart = 1;

    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");

    private readonly object _gate = new();
    private readonly Dictionary<int, ProcessFrames> _processes = new();
    private TraceEventSession? _session;

    public static bool CanTrace => AppRelauncher.IsElevated;

    /// <summary>Starts the trace session and its reader thread. False (with a message) when that is not possible.</summary>
    public bool TryStart(out string? error)
    {
        error = null;
        if (_session is not null)
        {
            return true;
        }

        if (!CanTrace)
        {
            error = "Reading FPS of protected games needs administrator rights.";
            return false;
        }

        try
        {
            var session = new TraceEventSession(SessionName);
            session.EnableProvider(DxgiProvider, TraceEventLevel.Verbose, ulong.MaxValue);
            session.EnableProvider(D3D9Provider, TraceEventLevel.Verbose, ulong.MaxValue);
            session.Source.AllEvents += OnEvent;
            _session = session;
            new Thread(() =>
            {
                try
                {
                    session.Source.Process();
                }
                catch
                {
                    // The session was stopped from Dispose.
                }
            })
            { IsBackground = true, Name = "ThermalScope ETW FPS" }.Start();
            return true;
        }
        catch (Exception exception)
        {
            error = $"Could not start the Windows FPS trace: {exception.Message}";
            return false;
        }
    }

    public FpsSample? ReadFps(int processId)
    {
        lock (_gate)
        {
            if (!_processes.TryGetValue(processId, out var frames) || frames.Published is not { } sample)
            {
                return null;
            }

            return NowMs() - frames.LastEventMs > StaleMs ? null : sample;
        }
    }

    /// <summary>The most recent frame intervals in milliseconds, oldest first.</summary>
    public float[] ReadFrameTimes(int processId, int count)
    {
        lock (_gate)
        {
            if (!_processes.TryGetValue(processId, out var frames) || NowMs() - frames.LastEventMs > StaleMs)
            {
                return Array.Empty<float>();
            }

            var available = (int)Math.Min(frames.Total, RingSize);
            var n = Math.Min(Math.Max(count, 0), available);
            var result = new float[n];
            var start = frames.Total - n;
            for (var i = 0; i < n; i++)
            {
                result[i] = frames.Ring[(start + i) % RingSize];
            }

            return result;
        }
    }

    public void Dispose()
    {
        var session = _session;
        _session = null;
        try
        {
            session?.Dispose();
        }
        catch
        {
            // Already stopped.
        }
    }

    private static double NowMs() => Environment.TickCount64;

    private void OnEvent(TraceEvent data)
    {
        var id = (int)data.ID;
        var isPresent =
            (data.ProviderGuid == DxgiProvider && (id == DxgiPresentStart || id == DxgiPresentMultiplaneStart)) ||
            (data.ProviderGuid == D3D9Provider && id == D3D9PresentStart);
        if (!isPresent)
        {
            return;
        }

        var timestamp = data.TimeStampRelativeMSec;
        lock (_gate)
        {
            if (!_processes.TryGetValue(data.ProcessID, out var frames))
            {
                if (_processes.Count > 64)
                {
                    return; // bound the table; real games present continuously, stragglers are not interesting
                }

                _processes[data.ProcessID] = frames = new ProcessFrames();
            }

            frames.Add(timestamp, NowMs());
        }
    }

    private sealed class ProcessFrames
    {
        public readonly float[] Ring = new float[RingSize];
        public long Total;
        public double LastEventMs;
        public FpsSample? Published;

        private double _lastStamp = double.NaN;
        private double _windowMs;
        private double _windowSlowest;
        private int _windowFrames;

        public void Add(double stampMs, double nowMs)
        {
            LastEventMs = nowMs;
            var delta = stampMs - _lastStamp;
            _lastStamp = stampMs;
            if (double.IsNaN(delta) || delta <= 0 || delta > StaleMs)
            {
                _windowMs = _windowSlowest = 0;
                _windowFrames = 0;
                return;
            }

            Ring[Total % RingSize] = (float)delta;
            Total++;
            _windowMs += delta;
            _windowFrames++;
            _windowSlowest = Math.Max(_windowSlowest, delta);
            if (_windowMs >= PublishWindowMs)
            {
                Published = new FpsSample(_windowFrames * 1000.0 / _windowMs, 1000.0 / _windowSlowest, _windowMs / _windowFrames);
                _windowMs = _windowSlowest = 0;
                _windowFrames = 0;
            }
        }
    }
}
