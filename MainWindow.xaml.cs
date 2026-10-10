using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using ThermalScope.Models;
using ThermalScope.Services;
using ThermalScope.ViewModels;

namespace ThermalScope;

/// <summary>Main window: header, Monitor view and Frame pacer view.</summary>
public partial class MainWindow : Window
{
    private static readonly double[] RefreshIntervals = { 0.5, 1, 2, 5, 10 };

    private readonly MainViewModel _vm = new();
    private readonly HardwareMonitorService _monitor = new();
    private readonly GameMonitorService _game = new();
    private readonly PawnIoSetupManager _pawnIo = new();
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly HotkeyManager _hotkeys = new(AppSettings.Current.Hotkeys);
    private readonly Dictionary<HotkeyAction, Button> _hotkeyButtons = new();
    private HotkeyAction? _capturing;
    private readonly TrayIcon _tray = new();
    private bool _exiting;
    private bool _closing;
    private bool _installingDriver;
    private OverlayWindow? _overlay;
    private readonly UpdateService _updates = new();
    private UpdateInfo? _pendingUpdate;
    private bool _updating;

    public MainWindow(bool startInTray)
    {
        InitializeComponent();
        DataContext = _vm;
        RestoreSettings();

        BuildHotkeyRows();
        UpdateHotkeyText();
        ApplyScale();
        LookManager.ApplyBackdrop(this);
        UpdateLookDescription();
        SizeChanged += (_, _) => ApplyScale();
        UpdateCapabilities();

        _monitor.SnapshotReady += OnHardwareSnapshot;
        _monitor.MonitorError += OnHardwareError;
        _game.SnapshotReady += OnGameSnapshot;
        _game.MonitorError += OnGameError;
        ThemeManager.Changed += OnThemeChanged;
        StateChanged += OnStateChanged;

        _tray.ShowRequested += ShowFromTray;
        _tray.ToggleLimiterRequested += () => _game.SetLimiterEnabled(!_game.LimiterEnabled);
        _tray.StartupToggleRequested += () => SetStartup(!StartupManager.IsEnabled);
        _tray.ExitRequested += ExitApp;
        _tray.MenuOpening += () => UpdateTray();
        UpdateTray();

        _hotkeys.Pressed += OnHotkey;
        var failed = _hotkeys.Register(this);
        if (failed.Count > 0)
        {
            _vm.StatusText = $"Hotkeys already used by another app: {string.Join(", ", failed)}";
        }

        AutoUpdateSwitch.IsChecked = _settings.AutoCheckUpdates;
        UpdateStatus.Text = $"Version {UpdateService.CurrentVersionText}. Not checked yet.";
        _ = AutoCheckForUpdatesAsync();
        _monitor.Start(RefreshIntervals.Contains(_settings.IntervalSeconds) ? _settings.IntervalSeconds : 2);
        _game.Start();
    }

    /// <summary>Brings the window back from the tray (or from behind other windows).</summary>
    public void ShowFromTray()
    {
        if (_closing)
        {
            return;
        }

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    // ----- Settings ---------------------------------------------------------------------------

    private void RestoreSettings()
    {
        _vm.UseFahrenheit = _settings.UseFahrenheit;
        _vm.SensorFilter = MainViewModel.SensorFilters.Contains(_settings.SensorFilter) ? _settings.SensorFilter : "All";

        FilterAll.IsChecked = _vm.SensorFilter == "All";
        FilterCpu.IsChecked = _vm.SensorFilter == "CPU";
        FilterGpu.IsChecked = _vm.SensorFilter == "GPU";
        FilterTemps.IsChecked = _vm.SensorFilter == "Temps";

        SelectChip(ThemeChips, _settings.Theme);
        SelectChip(UnitChips, _settings.UseFahrenheit ? "F" : "C");
        SelectChip(IntervalChips, (RefreshIntervals.Contains(_settings.IntervalSeconds) ? _settings.IntervalSeconds : 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
        SelectChip(LookChips, _settings.Style);
        SelectChip(AccentChips, _settings.Accent);
        SelectChip(GaugeChips, _settings.GaugeMode);
        SelectChip(ScaleChips, _settings.UiScale);
        SelectChip(OverlayScaleChips, _settings.OverlayScale);
        StartupSwitch.IsChecked = StartupManager.IsEnabled;
        StartupSwitch.IsEnabled = StartupManager.CanEnable;
        TraySwitch.IsChecked = _settings.CloseToTray;
        OverlaySwitch.IsChecked = _settings.OverlayEnabled;

        PinButton.IsChecked = _settings.AlwaysOnTop;
        Topmost = _settings.AlwaysOnTop;

        if (_settings.WindowWidth is { } width && _settings.WindowHeight is { } height &&
            _settings.WindowLeft is { } left && _settings.WindowTop is { } top &&
            IsOnScreen(left, top, width, height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
            Width = Math.Max(width, MinWidth);
            Height = Math.Max(height, MinHeight);
        }

        if (_settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        var screen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var window = new Rect(left, top, width, height);
        window.Intersect(screen);
        return !window.IsEmpty && window.Width > 200 && window.Height > 120;
    }

    private void SaveWindowBounds()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _settings.WindowLeft = bounds.Left;
        _settings.WindowTop = bounds.Top;
        _settings.WindowWidth = bounds.Width;
        _settings.WindowHeight = bounds.Height;
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        _settings.Save();
    }

    // ----- Snapshots --------------------------------------------------------------------------

    private void OnHardwareSnapshot(MonitorSnapshot snapshot)
    {
        if (_closing)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing)
            {
                return;
            }

            _vm.Apply(snapshot);
            UpdateTray();
        }));
    }

    private void OnHardwareError(string message)
    {
        if (_closing)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_closing)
            {
                _vm.ReportBackendIssue(message);
            }
        }));
    }

    private void OnGameSnapshot(GameMonitorSnapshot snapshot)
    {
        if (_closing)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing)
            {
                return;
            }

            _vm.Pacer.Apply(snapshot);
            UpdateOverlay(snapshot);
            UpdateTray();
        }));
    }

    // The strip appears only while the paced game is the window you are looking at.
    private void UpdateOverlay(GameMonitorSnapshot snapshot)
    {
        var show = _settings.OverlayEnabled && snapshot.MonitorReady &&
                   snapshot.SelectedProcess is { } game && OverlayWindow.IsForeground(game.ProcessId);
        if (show)
        {
            _overlay ??= new OverlayWindow(_vm);
            _overlay.ScaleSetting = _settings.OverlayScale;
            _overlay.ShowFor(snapshot.SelectedProcess!.ProcessId);
        }
        else
        {
            _overlay?.HideOverlay();
        }
    }

    // ----- Updates ------------------------------------------------------------------------------

    private async Task AutoCheckForUpdatesAsync()
    {
        if (!_settings.AutoCheckUpdates ||
            (_settings.LastUpdateCheck is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(12)))
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(8)); // let the app settle before touching the network
        await CheckForUpdatesAsync(manual: false);
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_closing || _updating)
        {
            return;
        }

        CheckUpdatesButton.IsEnabled = false;
        if (manual)
        {
            UpdateStatus.Text = "Checking...";
        }

        try
        {
            var update = await _updates.CheckAsync();
            _settings.LastUpdateCheck = DateTimeOffset.UtcNow;
            _settings.Save();
            if (_closing)
            {
                return;
            }

            _pendingUpdate = update;
            UpdateChip.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
            InstallUpdateButton.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
            if (update is null)
            {
                UpdateStatus.Text = $"Version {UpdateService.CurrentVersionText} is the latest.";
            }
            else
            {
                UpdateChip.Content = $"Update {update.Version.ToString(3)}";
                InstallUpdateButton.Content = update.IsDelta ? $"Install update ({update.SizeText}, changes only)" : $"Install update ({update.SizeText})";
                UpdateStatus.Text = $"Version {update.Version.ToString(3)} is available (you have {UpdateService.CurrentVersionText}).";
            }
        }
        catch (Exception exception)
        {
            if (manual && !_closing)
            {
                UpdateStatus.Text = $"Could not check for updates: {exception.Message}";
            }
        }
        finally
        {
            if (!_closing)
            {
                CheckUpdatesButton.IsEnabled = true;
            }
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(manual: true);

    private void AutoUpdateSwitch_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoCheckUpdates = AutoUpdateSwitch.IsChecked == true;
        _settings.Save();
    }

    private void UpdateChip_Click(object sender, RoutedEventArgs e)
    {
        // Opens Settings > General where the details and the install button are.
        TabGeneral.IsChecked = true;
        SettingsTab_Click(TabGeneral, e);
        SettingsPopup.IsOpen = true;
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is not { } update || _updating)
        {
            return;
        }

        if (!UpdateService.CanSelfUpdate)
        {
            // Run from source or a read-only folder: hand over to the release page instead.
            UpdateStatus.Text = "This copy cannot update itself (not installed with Setup). Opening the release page...";
            try { Process.Start(new ProcessStartInfo(update.PageUrl) { UseShellExecute = true }); } catch { /* no browser */ }
            return;
        }

        _updating = true;
        InstallUpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => UpdateStatus.Text = $"Downloading {update.Version.ToString(3)}... {p:P0}");
            var package = await _updates.DownloadAsync(update, progress);
            UpdateStatus.Text = "Installing and restarting...";
            if (UpdateService.StartApply(package))
            {
                ExitApp();
                return;
            }

            UpdateStatus.Text = "Could not start the update step.";
        }
        catch (Exception exception)
        {
            UpdateStatus.Text = $"Update failed: {exception.Message}";
        }
        finally
        {
            _updating = false;
            if (!_closing)
            {
                InstallUpdateButton.IsEnabled = true;
            }
        }
    }

    private void OverlayScale_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            _settings.OverlayScale = tag;
            _settings.Save();
            _game.RequestRefresh();
        }
    }

    private void OverlaySwitch_Click(object sender, RoutedEventArgs e) => SetOverlayEnabled(OverlaySwitch.IsChecked == true);

    private void SetOverlayEnabled(bool enabled)
    {
        _settings.OverlayEnabled = enabled;
        _settings.Save();
        OverlaySwitch.IsChecked = enabled;
        if (!enabled)
        {
            _overlay?.HideOverlay();
        }

        _game.RequestRefresh();
    }

    private void OnGameError(string message)
    {
        // Surfaced in the pacer status line on the next snapshot; nothing else to do here.
    }

    private void OnThemeChanged() => Dispatcher.BeginInvoke(new Action(() =>
    {
        _vm.ReapplyTheme();
        LookManager.ApplyBackdrop(this);
    }));

    private void UpdateCapabilities()
    {
        var elevated = AppRelauncher.IsElevated;
        _vm.SetCapabilities(_pawnIo.IsInstalled, elevated);
        ElevationText.Text = elevated ? "Running as administrator" : "Standard user";
    }

    private void UpdateTray()
    {
        var pacer = _vm.Pacer;
        var text = pacer.IsLive && pacer.TargetFps > 0 && pacer.LimiterEnabled
            ? $"Limiting {pacer.GameName} to {pacer.TargetFps} FPS"
            : $"CPU {_vm.Cpu.TempValue}{_vm.Cpu.TempUnit}   GPU {_vm.Gpu.TempValue}{_vm.Gpu.TempUnit}";
        _tray.Update(text, pacer.LimiterEnabled, StartupManager.IsEnabled, StartupManager.CanEnable);
    }

    // ----- Hotkeys ----------------------------------------------------------------------------

    private void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ToggleLimiter:
                _game.SetLimiterEnabled(!_game.LimiterEnabled);
                break;
            case HotkeyAction.IncreaseLimit:
                _game.NudgeLimit(+5);
                break;
            case HotkeyAction.DecreaseLimit:
                _game.NudgeLimit(-5);
                break;
            case HotkeyAction.ShowWindow:
                ShowFromTray();
                break;
            case HotkeyAction.ToggleOverlay:
                SetOverlayEnabled(!_settings.OverlayEnabled);
                break;
        }
    }

    private void UpdateHotkeyText() =>
        _vm.Pacer.HotkeyText = string.Join(
            Environment.NewLine,
            _hotkeys.Bindings.Select(b => $"{(b.IsAssigned ? b.Text : "Not set")}   {HotkeyManager.Describe(b.Action)}"));

    // ----- Hotkey settings: click a shortcut, press the new keys ---------------------------------

    private void BuildHotkeyRows()
    {
        HotkeyRows.Children.Clear();
        _hotkeyButtons.Clear();
        foreach (var binding in _hotkeys.Bindings)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
            {
                Text = HotkeyManager.Describe(binding.Action),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 12, 0)
            };
            var button = new Button { Tag = binding.Action, MinWidth = 150, Style = (Style)FindResource("GhostButton") };
            button.Click += HotkeyButton_Click;
            button.PreviewKeyDown += HotkeyButton_PreviewKeyDown;
            button.LostKeyboardFocus += (_, _) => EndCapture();
            System.Windows.Automation.AutomationProperties.SetName(button, $"Shortcut: {HotkeyManager.Describe(binding.Action)}");
            Grid.SetColumn(button, 1);
            row.Children.Add(label);
            row.Children.Add(button);
            HotkeyRows.Children.Add(row);
            _hotkeyButtons[binding.Action] = button;
        }

        RefreshHotkeyRows();
    }

    private void RefreshHotkeyRows()
    {
        foreach (var (action, button) in _hotkeyButtons)
        {
            var binding = _hotkeys.Get(action);
            button.Content = _capturing == action ? "Press keys…" : binding.IsAssigned ? binding.Text : "Not set";
        }
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: HotkeyAction action } && _capturing != action)
        {
            _capturing = action;
            _hotkeys.Suspend(); // so the current shortcuts do not fire while the new one is typed
            HotkeyStatus.Text = string.Empty;
            RefreshHotkeyRows();
        }
    }

    private void HotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturing is not { } action)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.None)
        {
            return; // a modifier on its own: wait for the main key
        }

        if (key == Key.Escape)
        {
            EndCapture();
            return;
        }

        uint modifiers = 0;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) modifiers |= HotkeyBinding.Control;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) modifiers |= HotkeyBinding.Alt;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) modifiers |= HotkeyBinding.Shift;

        if (modifiers == 0 && key is Key.Back or Key.Delete)
        {
            CommitHotkey(HotkeyBinding.Unassigned(action));
            return;
        }

        if (modifiers == 0)
        {
            HotkeyStatus.Text = "Add Ctrl, Alt or Shift so the shortcut does not steal normal typing.";
            return;
        }

        CommitHotkey(new HotkeyBinding(action, modifiers, (uint)KeyInterop.VirtualKeyFromKey(key)));
    }

    private void CommitHotkey(HotkeyBinding binding)
    {
        if (!_hotkeys.Rebind(binding, out var error))
        {
            HotkeyStatus.Text = error; // stay in capture mode so another combination can be tried
            return;
        }

        _settings.Hotkeys = _hotkeys.Export();
        _settings.Save();
        HotkeyStatus.Text = string.Empty;
        UpdateHotkeyText();
        EndCapture();
    }

    private void EndCapture()
    {
        if (_capturing is null)
        {
            return;
        }

        _capturing = null;
        var failed = _hotkeys.Resume();
        if (failed.Count > 0)
        {
            HotkeyStatus.Text = $"Already used by another app: {string.Join(", ", failed)}";
        }

        RefreshHotkeyRows();
    }

    private void HotkeyReset_Click(object sender, RoutedEventArgs e)
    {
        EndCapture();
        var failed = _hotkeys.ResetToDefaults();
        _settings.Hotkeys = _hotkeys.Export();
        _settings.Save();
        HotkeyStatus.Text = failed.Count > 0 ? $"Already used by another app: {string.Join(", ", failed)}" : string.Empty;
        UpdateHotkeyText();
        RefreshHotkeyRows();
    }

    private void SettingsPopup_Closed(object? sender, EventArgs e) => EndCapture();

    // ----- Look, scale -----------------------------------------------------------------------------

    private void SettingsTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            LookPanel.Visibility = tag == "Look" ? Visibility.Visible : Visibility.Collapsed;
            GeneralPanel.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
            HotkeysPanel.Visibility = tag == "Hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Look_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            _settings.Style = tag;
            _settings.Save();
            ApplyLook();
        }
    }

    private void Accent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            _settings.Accent = tag;
            _settings.Save();
            ApplyLook();
        }
    }

    private void Gauge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            _settings.GaugeMode = tag;
            _settings.Save();
            ApplyLook();
        }
    }

    private void Scale_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            _settings.UiScale = tag;
            _settings.Save();
            ApplyScale();
        }
    }

    private void ApplyLook()
    {
        LookManager.Apply(_settings.Style, _settings.Accent, _settings.GaugeMode);
        LookManager.ApplyBackdrop(this);
        UpdateLookDescription();
    }

    private void UpdateLookDescription() => LookDescription.Text = _settings.Style switch
    {
        "Scope" => "Viewfinder corner marks, a fine dot grid and sharp edges. The thermal-camera look.",
        "Glass" => "Frosted translucent panels over a blurred backdrop (Windows 11).",
        "Depth" => "Raised panels with soft shadows and a lit top edge.",
        _ => "Quiet, borderless panels with nothing extra."
    };

    private static void SelectChip(Panel panel, string tag)
    {
        foreach (var chip in panel.Children.OfType<RadioButton>())
        {
            chip.IsChecked = (string?)chip.Tag == tag;
        }
    }

    // Auto follows the window (so the layout stays proportional on small windows and 4K alike); the fixed
    // sizes pin it. The layout transform shrinks the available size accordingly, so cards still reflow.
    private void ApplyScale()
    {
        double scale;
        if (_settings.UiScale.EndsWith('%') &&
            double.TryParse(_settings.UiScale.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            scale = percent / 100.0;
        }
        else
        {
            scale = Math.Clamp(Math.Min(ActualWidth / 1180.0, ActualHeight / 760.0), 0.9, 2.0);
        }

        scale = Math.Round(scale, 2);
        if (Root.LayoutTransform is ScaleTransform current && Math.Abs(current.ScaleX - scale) < 0.005)
        {
            return;
        }

        Root.LayoutTransform = new ScaleTransform(scale, scale);
        MinWidth = 900 * Math.Max(scale, 0.9);
        MinHeight = 600 * Math.Max(scale, 0.9);
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            chrome.CaptionHeight = 52 * scale;
        }
    }

    // ----- Frame limit controls ---------------------------------------------------------------

    private void ApplyLimit(int fps)
    {
        _game.SetLimit(fps);
        _vm.Pacer.TargetInput = fps > 0 ? fps.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    private int ParseTarget()
    {
        var text = _vm.Pacer.TargetInput.Trim();
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var value = ParseTarget();
        ApplyLimit(value <= 0 ? 0 : Math.Clamp(value, GameMonitorService.MinFps, GameMonitorService.MaxFps));
        Keyboard.ClearFocus();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var fps))
        {
            ApplyLimit(fps);
        }
    }

    private void StepUp_Click(object sender, RoutedEventArgs e) => Step(+5);

    private void StepDown_Click(object sender, RoutedEventArgs e) => Step(-5);

    private void Step(int delta)
    {
        var current = ParseTarget();
        if (current <= 0)
        {
            current = _vm.Pacer.TargetFps > 0 ? _vm.Pacer.TargetFps : 60;
        }

        ApplyLimit(Math.Clamp(current + delta, GameMonitorService.MinFps, GameMonitorService.MaxFps));
    }

    private void TargetBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void TargetBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Apply_Click(sender, e);
            e.Handled = true;
        }
    }

    private void TargetBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _vm.Pacer.SetEditing(true);
        TargetBox.SelectAll();
    }

    private void TargetBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => _vm.Pacer.SetEditing(false);

    private void LimiterSwitch_Click(object sender, RoutedEventArgs e) =>
        _game.SetLimiterEnabled(LimiterSwitch.IsChecked == true);

    private void AutoDetect_Click(object sender, RoutedEventArgs e) => _game.UseAutoDetect();

    private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameList.SelectedItem is ProcessRowViewModel row)
        {
            _game.Configure(row.Name, autoMonitor: true);
            GameList.SelectedItem = null;
            PickGameButton.IsChecked = false;
        }
    }

    private void Consent_Click(object sender, RoutedEventArgs e)
    {
        _settings.FpsConsentAccepted = true;
        _settings.Save();
        _game.RequestRefresh();
    }

    // ----- Monitor view -----------------------------------------------------------------------

    private void SensorFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            _vm.SensorFilter = tag;
            _settings.SensorFilter = tag;
            _settings.Save();
        }
    }

    private async void InstallDriver_Click(object sender, RoutedEventArgs e)
    {
        if (_installingDriver)
        {
            return;
        }

        _installingDriver = true;
        _vm.StatusText = "Installing the PawnIO driver… approve the Windows prompt.";
        var result = await _pawnIo.InstallAsync();
        _installingDriver = false;
        if (_closing)
        {
            return;
        }

        UpdateCapabilities();
        if (!result.Success)
        {
            MessageBox.Show(this, result.Message, "PawnIO setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (AppRelauncher.TryRelaunch(elevated: true))
        {
            ExitApp();
        }
        else
        {
            _vm.StatusText = "PawnIO is installed. Restart ThermalScope as administrator to read the CPU temperature.";
        }
    }

    private void RestartAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (AppRelauncher.TryRelaunch(elevated: true))
        {
            ExitApp();
        }
        else
        {
            _vm.StatusText = "Administrator access was cancelled.";
        }
    }

    // ----- Header and settings popup ----------------------------------------------------------

    private void NavMonitor_Checked(object sender, RoutedEventArgs e) => _vm.ActiveView = "Monitor";

    private void NavPacer_Checked(object sender, RoutedEventArgs e) => _vm.ActiveView = "Pacer";

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshAll();

    private void RefreshAll()
    {
        _monitor.RequestRefresh();
        _game.RequestRefresh();
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        Topmost = PinButton.IsChecked == true;
        _settings.AlwaysOnTop = Topmost;
        _settings.Save();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        StartupSwitch.IsChecked = StartupManager.IsEnabled;
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            _settings.Theme = tag;
            _settings.Save();
            ThemeManager.Apply(tag);
            LookManager.ApplyBackdrop(this);
        }
    }

    private void Unit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
        {
            _vm.UseFahrenheit = tag == "F";
            _settings.UseFahrenheit = _vm.UseFahrenheit;
            _settings.Save();
        }
    }

    private void Interval_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            _monitor.SetInterval(seconds);
            _settings.IntervalSeconds = seconds;
            _settings.Save();
        }
    }

    private void StartupSwitch_Click(object sender, RoutedEventArgs e) =>
        SetStartup(StartupSwitch.IsChecked == true);

    private void SetStartup(bool enabled)
    {
        var ok = StartupManager.SetEnabled(enabled);
        StartupSwitch.IsChecked = StartupManager.IsEnabled;
        if (!ok)
        {
            _vm.StatusText = "Could not change the startup entry (run the published ThermalScope.exe, not dotnet run).";
        }

        UpdateTray();
    }

    private void TraySwitch_Click(object sender, RoutedEventArgs e)
    {
        _settings.CloseToTray = TraySwitch.IsChecked == true;
        _settings.Save();
    }

    // ----- Window chrome ----------------------------------------------------------------------

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnStateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        // A maximised chrome-less window overhangs the screen by its resize border.
        Root.Margin = maximized ? new Thickness(8) : new Thickness(0);
        MaximizeGlyph.Text = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.System && e.SystemKey == Key.F4)
        {
            Close(); // the Glass look drops WS_SYSMENU, which also drops the built-in Alt+F4
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5)
        {
            RefreshAll();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.D1 or Key.D2)
        {
            _vm.ActiveView = e.Key == Key.D1 ? "Monitor" : "Pacer";
            e.Handled = true;
        }
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_settings.CloseToTray && !_exiting && !_closing)
        {
            e.Cancel = true;
            SaveWindowBounds();
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        SaveWindowBounds();
        ThemeManager.Changed -= OnThemeChanged;
        _monitor.SnapshotReady -= OnHardwareSnapshot;
        _monitor.MonitorError -= OnHardwareError;
        _game.SnapshotReady -= OnGameSnapshot;
        _game.MonitorError -= OnGameError;
        _hotkeys.Dispose();
        _overlay?.Close();
        _tray.Dispose();
        _monitor.Dispose();
        _game.Dispose(); // also lifts the frame limit inside the game
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
