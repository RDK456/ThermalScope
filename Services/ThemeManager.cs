using System.Windows;
using Microsoft.Win32;

namespace ThermalScope.Services;

/// <summary>Dark is the base palette in Theme.xaml; Light overrides the colour tokens on top of it.</summary>
public static class ThemeManager
{
    public static readonly string[] Choices = { "System", "Dark", "Light" };

    private static string _choice = "System";

    public static bool IsLight { get; private set; }

    public static event Action? Changed;

    static ThemeManager()
    {
        // Only acts while "System" is chosen; explicit Dark/Light ignore OS changes.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (_choice == "System" && e.Category == UserPreferenceCategory.General && Application.Current is { } app)
            {
                app.Dispatcher.BeginInvoke(new Action(() => Apply("System")));
            }
        };
    }

    public static void Apply(string choice)
    {
        _choice = choice;
        var wantLight = choice switch
        {
            "Light" => true,
            "Dark" => false,
            _ => SystemUsesLightTheme()
        };

        IsLight = wantLight;
        LookManager.Refresh();
        Changed?.Invoke();
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }
}
