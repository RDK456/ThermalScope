using System.Diagnostics;
using System.IO;
using System.Reflection;
using LibreHardwareMonitor.PawnIo;

namespace ThermalScope.Services;

public sealed class PawnIoSetupManager
{
    private const string InstallerResourceName = "ThermalScope.install-pawnio.ps1";
    private readonly string _stateDirectory;
    private readonly string _promptStatePath;

    public PawnIoSetupManager()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = AppContext.BaseDirectory;
        }

        _stateDirectory = Path.Combine(localAppData, "ThermalScope");
        _promptStatePath = Path.Combine(_stateDirectory, "pawnio-prompted.txt");
    }

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

    public bool ShouldPrompt => !IsInstalled && !File.Exists(_promptStatePath);

    public void MarkPrompted()
    {
        try
        {
            Directory.CreateDirectory(_stateDirectory);
            File.WriteAllText(_promptStatePath, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch
        {
            // A read-only profile should not prevent the monitor from starting.
        }
    }

    public async Task<PawnIoSetupResult> InstallAsync()
    {
        string scriptPath;
        try
        {
            scriptPath = ExtractInstallerScript();
        }
        catch (Exception exception)
        {
            return new PawnIoSetupResult(false, $"Could not prepare the PawnIO setup: {exception.Message}");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new PawnIoSetupResult(false, "Windows did not start the PawnIO installer.");
            }

            await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
            return process.ExitCode == 0
                ? new PawnIoSetupResult(true, "PawnIO setup completed. ThermalScope will restart to load the CPU sensors.")
                : new PawnIoSetupResult(false, $"The PawnIO installer exited with code {process.ExitCode}.");
        }
        catch (Exception exception)
        {
            return new PawnIoSetupResult(false, $"PawnIO setup was cancelled or failed: {exception.Message}");
        }
    }

    public bool TryRestart()
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
            if (isDotnetHost)
            {
                startInfo.Arguments = $"\"{entryAssembly}\"";
            }

            return Process.Start(startInfo) is not null;
        }
        catch
        {
            return false;
        }
    }

    private string ExtractInstallerScript()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "ThermalScope");
        Directory.CreateDirectory(tempDirectory);
        var extractedPath = Path.Combine(tempDirectory, "install-pawnio.ps1");

        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(InstallerResourceName);
        if (resource is null)
        {
            var localScript = Path.Combine(AppContext.BaseDirectory, "install-pawnio.ps1");
            if (File.Exists(localScript))
            {
                return localScript;
            }

            throw new FileNotFoundException("The embedded PawnIO setup script is missing.");
        }

        using var output = File.Create(extractedPath);
        resource.CopyTo(output);
        return extractedPath;
    }
}

public readonly record struct PawnIoSetupResult(bool Success, string Message);
