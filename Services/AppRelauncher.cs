using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;

namespace ThermalScope.Services;

internal static class AppRelauncher
{
    public const string RelaunchArgument = "--relaunch";

    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Starts a new copy of the app (optionally elevated via UAC). Returns false if it could not start.</summary>
    public static bool TryRelaunch(bool elevated)
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                return false;
            }

            var isDotnetHost = string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase);
            var entryAssembly = Assembly.GetEntryAssembly()?.Location;
            if (isDotnetHost && string.IsNullOrWhiteSpace(entryAssembly))
            {
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            };
            // The new copy waits for this one to exit before it takes the single-instance lock.
            var relaunch = $"{RelaunchArgument} {Environment.ProcessId}";
            startInfo.Arguments = isDotnetHost ? $"\"{entryAssembly}\" {relaunch}" : relaunch;

            if (elevated)
            {
                startInfo.Verb = "runas";
            }

            return Process.Start(startInfo) is not null;
        }
        catch
        {
            // Includes the user declining the UAC prompt.
            return false;
        }
    }
}
