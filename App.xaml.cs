using System.IO;
using System.Windows;
using System.Windows.Threading;
using ThermalScope.Services;

namespace ThermalScope;

/// <summary>Application entry: single instance, crash logging, theme.</summary>
public partial class App : Application
{
    private const string InstanceName = @"Local\ThermalScope.SingleInstance";
    private const string ShowEventName = @"Local\ThermalScope.Show";

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ThermalScope",
        "thermal-scope.log");

    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        WaitForPreviousInstance(e.Args);
        _singleInstance = new Mutex(true, InstanceName, out var isFirstInstance);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!isFirstInstance)
        {
            // Two copies would fight over the helper's shared memory; wake the running one instead.
            _showEvent.Set();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var settings = AppSettings.Current;
        LookManager.Apply(settings.Style, settings.Accent, settings.GaugeMode);
        ThemeManager.Apply(settings.Theme);

        var startInTray = e.Args.Contains(StartupManager.TrayArgument, StringComparer.OrdinalIgnoreCase);
        var window = new MainWindow(startInTray);
        MainWindow = window;
        if (!startInTray)
        {
            window.Show();
        }

        Task.Run(() =>
        {
            while (_showEvent.WaitOne())
            {
                Dispatcher.BeginInvoke(new Action(window.ShowFromTray));
            }
        });
    }

    private static void WaitForPreviousInstance(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals(AppRelauncher.RelaunchArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out var pid))
        {
            return;
        }

        try
        {
            using var previous = System.Diagnostics.Process.GetProcessById(pid);
            previous.WaitForExit(10_000);
        }
        catch
        {
            // Already gone.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteException(e.Exception);
        e.Handled = true; // a failed refresh must not take the whole app (and the game limiter) down
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteException(exception);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteException(e.Exception);
        e.SetObserved();
    }

    private static void WriteException(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(
                LogPath,
                $"[{DateTimeOffset.Now:O}]{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never mask the original exception.
        }
    }
}
