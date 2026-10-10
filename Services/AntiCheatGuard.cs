using System.Diagnostics;
using System.IO;

namespace ThermalScope.Services;

/// <summary>
/// Decides whether a process is off limits for the FPS helper: our own process, Windows system
/// processes, and anything protected by a known anti-cheat. Injecting into those gets accounts
/// banned or destabilises Windows, so the helper is simply never attached.
/// </summary>
internal static class AntiCheatGuard
{
    private static readonly string[] AntiCheatProcesses =
    {
        "easyanticheat", "easyanticheat_eos", "beservice", "beservice_x64",
        "vgc", "eaanticheat.gameservice", "equ8_helper", "xigncodeservice", "gameguard"
    };

    private static readonly string[] AntiCheatModulePrefixes =
    {
        "easyanticheat", "eosanticheat", "beclient", "battleye", "equ8", "xigncode", "npggnt", "gameguard"
    };

    /// <summary>Returns a user-facing reason when the process must not be hooked, otherwise null.</summary>
    public static string? FindBlocker(int processId)
    {
        if (processId == Environment.ProcessId)
        {
            return "ThermalScope cannot monitor itself.";
        }

        Process target;
        try
        {
            target = Process.GetProcessById(processId);
        }
        catch
        {
            return "The selected game process is no longer available.";
        }

        using (target)
        {
            if (IsWindowsProcess(target))
            {
                return $"{target.ProcessName}.exe is part of Windows and is never hooked.";
            }

            var running = FindRunningAntiCheat();
            if (running is not null)
            {
                return $"{running} anti-cheat is running. ThermalScope will not attach to protected games; use the Xbox Game Bar (Win+G) FPS counter instead.";
            }

            var module = FindAntiCheatModule(target);
            return module is null
                ? null
                : $"{module} anti-cheat is loaded in this game. ThermalScope will not attach to protected games; use the Xbox Game Bar (Win+G) FPS counter instead.";
        }
    }

    private static bool IsWindowsProcess(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            return path.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? FindRunningAntiCheat()
    {
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                foreach (var known in AntiCheatProcesses)
                {
                    if (name.Equals(known, StringComparison.OrdinalIgnoreCase))
                    {
                        return known switch
                        {
                            "vgc" => "Riot Vanguard",
                            "beservice" or "beservice_x64" => "BattlEye",
                            "eaanticheat.gameservice" => "EA Javelin",
                            _ when known.StartsWith("easyanticheat") => "Easy Anti-Cheat",
                            _ => known
                        };
                    }
                }
            }
            catch
            {
                // Exited while enumerating.
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    private static string? FindAntiCheatModule(Process target)
    {
        try
        {
            foreach (ProcessModule module in target.Modules)
            {
                var name = module.ModuleName;
                foreach (var prefix in AntiCheatModulePrefixes)
                {
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return prefix switch
                        {
                            "beclient" or "battleye" => "BattlEye",
                            "easyanticheat" or "eosanticheat" => "Easy Anti-Cheat",
                            _ => prefix
                        };
                    }
                }
            }
        }
        catch
        {
            // Protected processes refuse module enumeration; the injection itself will fail for those.
        }

        return null;
    }
}
