using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;

namespace ThermalScope.Services;

public sealed class GameFpsMonitor : IDisposable
{
    private const string HookResourceName = "ThermalScope.ThermalScopeHook.dll";
    private const string MappingName = @"Local\ThermalScopeFpsConfig";
    private const int ConfigSize = 32;
    private const int ConfigMagic = 0x54494D31;
    private const int ConfigVersion = 1;
    private readonly string _hookPath;
    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _view;
    private int _attachedProcessId;

    public GameFpsMonitor()
    {
        try
        {
            _hookPath = PrepareHookPath();
            IsAvailable = File.Exists(_hookPath);
            Status = IsAvailable
                ? "Standalone DirectX FPS monitor ready."
                : "The standalone FPS monitor DLL could not be prepared.";
        }
        catch (Exception exception)
        {
            _hookPath = Path.Combine(AppContext.BaseDirectory, "ThermalScopeHook.dll");
            IsAvailable = false;
            Status = $"The standalone FPS monitor could not be prepared: {exception.Message}";
        }
    }

    public bool IsAvailable { get; private set; }
    public string Status { get; private set; }
    public string HookPath => _hookPath;

    public bool TryAttach(int processId, out string error)
    {
        error = string.Empty;
        if (!IsAvailable)
        {
            error = Status;
            return false;
        }

        if (processId <= 0)
        {
            error = "The selected game process is no longer available.";
            return false;
        }

        try
        {
            EnsureMapping();
            WriteConfig(processId, true);
            if (_attachedProcessId != processId)
            {
                if (!NativeProcessInjector.Inject(processId, _hookPath, out error))
                {
                    Disable();
                    return false;
                }

                _attachedProcessId = processId;
            }

            for (var attempt = 0; attempt < 30; attempt++)
            {
                if (_view!.ReadInt32(24) == 1)
                {
                    Status = "Standalone DirectX FPS monitor attached.";
                    return true;
                }

                Thread.Sleep(100);
            }

            WriteConfig(processId, false);
            error = "The FPS monitor loaded, but no DirectX 11/12 swap chain was found yet. It will keep trying in the background.";
            Status = error;
            return false;
        }
        catch (Exception exception)
        {
            Disable();
            error = $"The FPS monitor could not start: {exception.Message}";
            Status = error;
            return false;
        }
    }

    public bool Disable()
    {
        try
        {
            if (_view is not null)
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

    public double? ReadFps(int processId)
    {
        if (_view is null || processId <= 0 || _view.ReadInt32(12) != processId || _view.ReadInt32(20) == 0)
        {
            return null;
        }

        var bits = _view.ReadInt32(28);
        var fps = BitConverter.Int32BitsToSingle(bits);
        return double.IsFinite(fps) && fps > 0 && fps <= 1000 ? fps : null;
    }

    public void Dispose()
    {
        Disable();
        _view?.Dispose();
        _view = null;
        _mapping?.Dispose();
        _mapping = null;
        IsAvailable = false;
    }

    private void EnsureMapping()
    {
        if (_view is not null)
        {
            return;
        }

        _mapping = MemoryMappedFile.CreateOrOpen(MappingName, 4096, MemoryMappedFileAccess.ReadWrite);
        _view = _mapping.CreateViewAccessor(0, ConfigSize, MemoryMappedFileAccess.ReadWrite);
    }

    private void WriteConfig(int targetProcessId, bool enabled)
    {
        var view = _view ?? throw new InvalidOperationException("The FPS monitor mapping is not open.");
        view.Write(0, ConfigMagic);
        view.Write(4, ConfigVersion);
        view.Write(8, Environment.ProcessId);
        view.Write(12, targetProcessId);
        view.Write(16, 0);
        view.Write(20, enabled ? 1 : 0);
        view.Write(24, 0);
        view.Write(28, 0);
        view.Flush();
    }

    private static string PrepareHookPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = AppContext.BaseDirectory;
        }

        var directory = Path.Combine(localAppData, "ThermalScope");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "ThermalScopeHook.dll");
        if (File.Exists(path))
        {
            return path;
        }

        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(HookResourceName);
        if (resource is not null)
        {
            using var output = File.Create(path);
            resource.CopyTo(output);
            return path;
        }

        var bundledPath = Path.Combine(AppContext.BaseDirectory, "ThermalScopeHook.dll");
        if (File.Exists(bundledPath))
        {
            File.Copy(bundledPath, path, true);
        }

        return path;
    }
}
