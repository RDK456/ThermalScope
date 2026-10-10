using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ThermalScope.Services;

internal sealed record DetectedGame(int ProcessId, string ExecutableName, string WindowTitle);

/// <summary>
/// Finds the game the user is playing: the foreground window of a process that loads a Direct3D/DXGI
/// runtime and covers its whole monitor (borderless or fullscreen). The result is sticky, so switching
/// to ThermalScope itself does not forget the game. A manual override always wins (see GameMonitorService).
/// </summary>
internal static class GameDetector
{
    private static readonly string[] GraphicsModules =
    {
        "d3d9.dll", "d3d10.dll", "d3d10_1.dll", "d3d11.dll", "d3d12.dll", "dxgi.dll"
    };

    // Apps that load DirectX for their own UI and are never games.
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "iexplore",
        "explorer", "code", "devenv", "discord", "spotify", "slack", "teams", "ms-teams",
        "obs64", "obs32", "steam", "steamwebhelper", "epicgameslauncher", "gog galaxy",
        "claude", "windowsterminal", "powershell", "cmd", "thermalscope", "vlc", "mpc-hc64", "mpc-be64"
    };

    private static readonly Dictionary<int, (bool IsGraphics, DateTime At)> ModuleCache = new();

    public static DetectedGame? FindForegroundGame(int ownProcessId)
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return null;
        }

        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == ownProcessId || !CoversMonitor(window))
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (NotGames.Contains(process.ProcessName) || IsInWindowsFolder(process) ||
                !LoadsGraphicsRuntime(process))
            {
                return null;
            }

            return new DetectedGame((int)processId, process.ProcessName + ".exe", GetTitle(window));
        }
        catch
        {
            return null; // Exited, or an elevated process we cannot inspect.
        }
    }

    private static bool CoversMonitor(IntPtr window)
    {
        if (!GetWindowRect(window, out var rect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(window, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        const int tolerance = 2;
        return rect.Left <= info.Monitor.Left + tolerance && rect.Top <= info.Monitor.Top + tolerance &&
               rect.Right >= info.Monitor.Right - tolerance && rect.Bottom >= info.Monitor.Bottom - tolerance;
    }

    private static bool IsInWindowsFolder(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return path is not null && path.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool LoadsGraphicsRuntime(Process process)
    {
        lock (ModuleCache)
        {
            if (ModuleCache.TryGetValue(process.Id, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(30))
            {
                return cached.IsGraphics;
            }
        }

        var found = false;
        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                if (Array.Exists(GraphicsModules, name => name.Equals(module.ModuleName, StringComparison.OrdinalIgnoreCase)))
                {
                    found = true;
                    break;
                }
            }
        }
        catch
        {
            // Cannot enumerate (protected or exiting): treat as not a hookable game.
        }

        lock (ModuleCache)
        {
            ModuleCache[process.Id] = (found, DateTime.UtcNow);
            if (ModuleCache.Count > 64)
            {
                ModuleCache.Clear();
            }
        }

        return found;
    }

    private static string GetTitle(IntPtr window)
    {
        var builder = new StringBuilder(256);
        return GetWindowText(window, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);
}
