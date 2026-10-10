using System.Drawing;
using System.Windows.Forms;

namespace ThermalScope.Services;

/// <summary>Notification-area icon with a small menu, so the limiter keeps working with the window closed.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon? _ownedIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _limiterItem;
    private readonly ToolStripMenuItem _startupItem;

    public TrayIcon()
    {
        _statusItem = new ToolStripMenuItem("ThermalScope") { Enabled = false };
        _limiterItem = new ToolStripMenuItem("Frame limiter on") { CheckOnClick = false };
        _limiterItem.Click += (_, _) => ToggleLimiterRequested?.Invoke();
        _startupItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = false };
        _startupItem.Click += (_, _) => StartupToggleRequested?.Invoke();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open ThermalScope", null, (_, _) => ShowRequested?.Invoke());
        menu.Items.Add(_limiterItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
        menu.Opening += (_, _) => MenuOpening?.Invoke();

        _ownedIcon = LoadOwnedIcon();
        _icon = new NotifyIcon
        {
            Icon = _ownedIcon ?? SystemIcons.Application,
            Text = "ThermalScope",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowRequested?.Invoke();
            }
        };
    }

    public event Action? ShowRequested;
    public event Action? ToggleLimiterRequested;
    public event Action? StartupToggleRequested;
    public event Action? ExitRequested;
    public event Action? MenuOpening;

    public void Update(string status, bool limiterOn, bool startsWithWindows, bool canEditStartup)
    {
        _statusItem.Text = status;
        _icon.Text = status.Length <= 63 ? status : status[..63]; // NotifyIcon.Text is capped at 63 characters
        _limiterItem.Checked = limiterOn;
        _startupItem.Checked = startsWithWindows;
        _startupItem.Enabled = canEditStartup;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _ownedIcon?.Dispose(); // a GDI handle from ExtractAssociatedIcon
    }

    private static Icon? LoadOwnedIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            return !string.IsNullOrEmpty(path) ? Icon.ExtractAssociatedIcon(path) : null;
        }
        catch
        {
            return null; // falls back to the stock icon
        }
    }
}
