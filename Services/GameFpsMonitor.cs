using ThermalScope.Models;

namespace ThermalScope.Services;

/// <summary>
/// Controller side of the in-game helper: injects ThermalScopeHook.dll into the chosen process and
/// talks to it through a shared-memory block (see native/ThermalScopeHook.cpp for the layout).
/// </summary>
public sealed class GameFpsMonitor : IDisposable
{
    private const string MappingName = @"Local\ThermalScopeFpsConfig3";
    private const int ConfigSize = 1024;
    private const int ConfigMagic = 0x54494D31;
    private const int ConfigVersion = 3;
    private const int FrameRingSize = 240;

    private const int OffsetController = 8;
    private const int OffsetTarget = 12;
    private const int OffsetEnabled = 20;
    private const int OffsetHookReady = 24;
    private const int OffsetAverage = 28;
    private const int OffsetLow = 32;
    private const int OffsetFrameTime = 36;
    private const int OffsetLimit = 44;
    private const int OffsetFrameCount = 48;
    private const int OffsetPacing = 52;
    private const int OffsetRefresh = 56;
    private const int OffsetFrames = 64;

    private readonly HookFileStore.HookFile? _hook;
    private readonly HookFileStore.HookFile? _hook32;
    private SecureSharedMemory? _memory;
    private int _attachedProcessId;
    private int _targetFps;
    private bool _pacingLocked;

    public GameFpsMonitor()
    {
        try
        {
            _hook = HookFileStore.Prepare();
            try
            {
                _hook32 = HookFileStore.Prepare(x86: true);
            }
            catch
            {
                // 64-bit games still work; 32-bit ones report that their helper is missing.
            }

            IsAvailable = true;
            Status = "Frame pacer ready.";
        }
        catch (Exception exception)
        {
            IsAvailable = false;
            Status = $"The frame pacer could not be prepared: {exception.Message}";
        }
    }

    public bool IsAvailable { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public int TargetFps => _targetFps;

    /// <summary>Injects the helper (once per process). Returns before the helper has hooked a swap chain.</summary>
    public bool TryAttach(int processId, out string error)
    {
        error = string.Empty;
        if (!IsAvailable || _hook is null)
        {
            error = Status;
            return false;
        }

        if (processId <= 0)
        {
            error = "The selected game process is no longer available.";
            return false;
        }

        var blocker = AntiCheatGuard.FindBlocker(processId);
        if (blocker is not null)
        {
            error = blocker;
            return false;
        }

        try
        {
            EnsureMemory();
            WriteConfig(processId, true);
            if (_attachedProcessId != processId)
            {
                if (!NativeProcessInjector.TryGetBitness(processId, out var is32Bit, out error))
                {
                    Disable();
                    return false;
                }

                var hook = is32Bit ? _hook32 : _hook;
                if (hook is null)
                {
                    error = "The 32-bit frame pacer helper is not available in this build.";
                    Disable();
                    return false;
                }

                using var locked = HookFileStore.OpenVerified(hook);
                if (!NativeProcessInjector.Inject(processId, hook.Path, is32Bit, out error))
                {
                    Disable();
                    return false;
                }

                _attachedProcessId = processId;
            }

            return true;
        }
        catch (Exception exception)
        {
            Disable();
            error = $"The frame pacer could not start: {exception.Message}";
            Status = error;
            return false;
        }
    }

    /// <summary>Lock to the display refresh (console-style) when the target divides it; otherwise the precise timer is used.</summary>
    public void SetPacingLocked(bool locked)
    {
        _pacingLocked = locked;
        _memory?.WriteInt32(OffsetPacing, locked ? 1 : 0);
    }

    /// <summary>Refresh rate (Hz) of the display the game is on, as seen by the helper; 0 until known.</summary>
    public int RefreshHz => _memory?.ReadInt32(OffsetRefresh) ?? 0;

    /// <summary>True once the helper has hooked a DirectX swap chain inside the game.</summary>
    public bool IsHookReady => _memory is not null && _memory.ReadInt32(OffsetHookReady) == 1;

    /// <summary>Sets the frame limit (0 = unlimited). Takes effect on the next frame, even mid-game.</summary>
    public void SetTargetFps(int fps)
    {
        _targetFps = Math.Clamp(fps, 0, 1000);
        _memory?.WriteInt32(OffsetLimit, _targetFps);
    }

    public bool Disable()
    {
        try
        {
            if (_memory is not null)
            {
                WriteConfig(0, false);
            }

            _attachedProcessId = 0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public FpsSample? ReadFps(int processId)
    {
        if (_memory is null || processId <= 0 ||
            _memory.ReadInt32(OffsetTarget) != processId || _memory.ReadInt32(OffsetEnabled) == 0)
        {
            return null;
        }

        var average = _memory.ReadSingle(OffsetAverage);
        var low = _memory.ReadSingle(OffsetLow);
        var frameTime = _memory.ReadSingle(OffsetFrameTime);
        if (!float.IsFinite(average) || average <= 0 || average > 20000)
        {
            return null;
        }

        return new FpsSample(average, float.IsFinite(low) ? low : 0, float.IsFinite(frameTime) ? frameTime : 0);
    }

    /// <summary>The most recent frame intervals in milliseconds, oldest first.</summary>
    public float[] ReadFrameTimes(int processId, int count)
    {
        if (_memory is null || processId <= 0 || _memory.ReadInt32(OffsetTarget) != processId)
        {
            return Array.Empty<float>();
        }

        var total = (uint)_memory.ReadInt32(OffsetFrameCount);
        var available = (int)Math.Min(total, FrameRingSize);
        var n = Math.Min(Math.Max(count, 0), available);
        var result = new float[n];
        var start = total - (uint)n;
        for (var i = 0; i < n; i++)
        {
            var value = _memory.ReadSingle(OffsetFrames + (int)((start + (uint)i) % FrameRingSize) * 4);
            result[i] = float.IsFinite(value) && value >= 0 && value < 1000 ? value : 0;
        }

        return result;
    }

    public void Dispose()
    {
        SetTargetFps(0);
        Disable();
        _memory?.Dispose();
        _memory = null;
        IsAvailable = false;
    }

    private void EnsureMemory()
    {
        _memory ??= SecureSharedMemory.Create(MappingName, ConfigSize);
    }

    private void WriteConfig(int targetProcessId, bool enabled)
    {
        var memory = _memory ?? throw new InvalidOperationException("The shared memory is not open.");
        memory.WriteInt32(0, ConfigMagic);
        memory.WriteInt32(4, ConfigVersion);
        memory.WriteInt32(OffsetController, Environment.ProcessId);
        memory.WriteInt32(OffsetTarget, targetProcessId);
        memory.WriteInt32(16, 0);
        memory.WriteInt32(OffsetPacing, _pacingLocked ? 1 : 0);
        memory.WriteInt32(OffsetRefresh, 0);
        memory.WriteInt32(OffsetEnabled, enabled ? 1 : 0);
        memory.WriteInt32(OffsetLimit, enabled ? _targetFps : 0);
        memory.WriteInt32(OffsetAverage, 0);
        memory.WriteInt32(OffsetLow, 0);
        memory.WriteInt32(OffsetFrameTime, 0);
    }
}
