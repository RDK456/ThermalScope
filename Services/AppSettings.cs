using System.IO;
using System.Text.Json;

namespace ThermalScope.Services;

/// <summary>User preferences persisted to %LOCALAPPDATA%\ThermalScope\settings.json (best effort).</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ThermalScope",
        "settings.json");

    public int IntervalSeconds { get; set; } = 2;
    public bool UseFahrenheit { get; set; }
    public string Theme { get; set; } = "System";
    public bool CloseToTray { get; set; }
    public bool OverlayEnabled { get; set; } = true;
    public string OverlayScale { get; set; } = "Auto";
    public bool AutoCheckUpdates { get; set; } = true;
    public DateTimeOffset? LastUpdateCheck { get; set; }
    public string Style { get; set; } = "Scope";
    public string Accent { get; set; } = "Teal";
    public string GaugeMode { get; set; } = "Ramp";
    public string UiScale { get; set; } = "Auto";

    /// <summary>Hotkey overrides: action name to shortcut text such as "Ctrl+Alt+F10" (empty = unassigned).</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();
    public bool AlwaysOnTop { get; set; }
    public string SensorFilter { get; set; } = "All";
    public bool FpsConsentAccepted { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    public static AppSettings Current { get; } = Load();

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // A read-only profile must not break the app.
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupt settings fall back to defaults.
        }

        return new AppSettings();
    }
}
