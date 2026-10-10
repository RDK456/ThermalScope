using Microsoft.Win32;

namespace ThermalScope.Services;

/// <summary>"Start with Windows": a per-user Run entry that launches ThermalScope silently into the tray.</summary>
internal static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ThermalScope";
    public const string TrayArgument = "--tray";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool CanEnable => IsPublishedExecutable(out _);

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            if (!IsPublishedExecutable(out var path))
            {
                return false;
            }

            key.SetValue(ValueName, $"\"{path}\" {TrayArgument}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    // `dotnet run` hosts the app through dotnet.exe, which is not something to register for startup.
    private static bool IsPublishedExecutable(out string path)
    {
        path = Environment.ProcessPath ?? string.Empty;
        return !string.IsNullOrEmpty(path) &&
               !string.Equals(System.IO.Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase);
    }
}
