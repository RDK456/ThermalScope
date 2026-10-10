using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using LibreHardwareMonitor.PawnIo;

namespace ThermalScope.Services;

/// <summary>
/// Optional, user-initiated install of the signed PawnIO driver. Real CPU die/package temperatures
/// (the same ones HWiNFO or Task Manager's vendors read) live in model-specific registers that only
/// a kernel driver can read; Windows has no driver-free API for them.
/// </summary>
public sealed class PawnIoSetupManager
{
    private const string InstallerResourceName = "ThermalScope.install-pawnio.ps1";

    public bool IsInstalled
    {
        get
        {
            try
            {
                return PawnIo.IsInstalled;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool IsElevated => AppRelauncher.IsElevated;

    public async Task<PawnIoSetupResult> InstallAsync()
    {
        string script;
        try
        {
            script = ReadInstallerScript();
        }
        catch (Exception exception)
        {
            return new PawnIoSetupResult(false, $"Could not prepare the PawnIO setup: {exception.Message}");
        }

        try
        {
            // The script travels on the command line instead of being written to a temp file, so there
            // is nothing on disk another process could replace before the elevated PowerShell runs it.
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = true,
                Verb = AppRelauncher.IsElevated ? string.Empty : "runas"
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new PawnIoSetupResult(false, "Windows did not start the PawnIO installer.");
            }

            await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
            return process.ExitCode == 0
                ? new PawnIoSetupResult(true, "PawnIO is installed. ThermalScope restarts as administrator to load the CPU sensors.")
                : new PawnIoSetupResult(false, $"The PawnIO installer exited with code {process.ExitCode}.");
        }
        catch (Exception exception)
        {
            return new PawnIoSetupResult(false, $"PawnIO setup was cancelled or failed: {exception.Message}");
        }
    }

    private static string ReadInstallerScript()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(InstallerResourceName)
            ?? throw new FileNotFoundException("The embedded PawnIO setup script is missing.");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

public readonly record struct PawnIoSetupResult(bool Success, string Message);
