using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ThermalScope.Services;

public enum HotkeyAction
{
    ToggleLimiter,
    IncreaseLimit,
    DecreaseLimit,
    ShowWindow,
    ToggleOverlay
}

/// <summary>A system-wide shortcut: modifier flags (MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4) plus a virtual-key code.</summary>
public sealed record HotkeyBinding(HotkeyAction Action, uint Modifiers, uint Key)
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;

    private static readonly (Key Key, string Name)[] KeyNames =
    {
        (System.Windows.Input.Key.Prior, "PgUp"), (System.Windows.Input.Key.Next, "PgDn"),
        (System.Windows.Input.Key.Return, "Enter"), (System.Windows.Input.Key.Escape, "Esc"),
        (System.Windows.Input.Key.Back, "Backspace"), (System.Windows.Input.Key.OemPlus, "Plus"),
        (System.Windows.Input.Key.OemMinus, "Minus"), (System.Windows.Input.Key.OemComma, "Comma"),
        (System.Windows.Input.Key.OemPeriod, "Period"), (System.Windows.Input.Key.Oem3, "Tilde")
    };

    public bool IsAssigned => Key != 0;

    public string Text
    {
        get
        {
            if (!IsAssigned)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if ((Modifiers & Control) != 0) parts.Add("Ctrl");
            if ((Modifiers & Alt) != 0) parts.Add("Alt");
            if ((Modifiers & Shift) != 0) parts.Add("Shift");
            parts.Add(KeyName(KeyInterop.KeyFromVirtualKey((int)Key)));
            return string.Join("+", parts);
        }
    }

    public bool SameKeys(HotkeyBinding other) => IsAssigned && other.IsAssigned && Modifiers == other.Modifiers && Key == other.Key;

    public static HotkeyBinding Unassigned(HotkeyAction action) => new(action, 0, 0);

    public static HotkeyBinding? TryParse(HotkeyAction action, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        uint modifiers = 0;
        Key key = System.Windows.Input.Key.None;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": modifiers |= Control; break;
                case "alt": modifiers |= Alt; break;
                case "shift": modifiers |= Shift; break;
                default:
                    var named = Array.Find(KeyNames, k => k.Name.Equals(part, StringComparison.OrdinalIgnoreCase));
                    if (named.Name is not null)
                    {
                        key = named.Key;
                    }
                    else if (part.Length == 1 && char.IsDigit(part[0]))
                    {
                        key = System.Windows.Input.Key.D0 + (part[0] - '0');
                    }
                    else if (!Enum.TryParse(part, true, out key))
                    {
                        return null;
                    }

                    break;
            }
        }

        var vk = key == System.Windows.Input.Key.None ? 0 : KeyInterop.VirtualKeyFromKey(key);
        return vk == 0 || modifiers == 0 ? null : new HotkeyBinding(action, modifiers, (uint)vk);
    }

    private static string KeyName(Key key)
    {
        var named = Array.Find(KeyNames, k => k.Key == key);
        if (named.Name is not null)
        {
            return named.Name;
        }

        return key >= System.Windows.Input.Key.D0 && key <= System.Windows.Input.Key.D9
            ? ((int)key - (int)System.Windows.Input.Key.D0).ToString()
            : key.ToString();
    }
}

/// <summary>System-wide, user-configurable hotkeys (they work while a game has focus).</summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    public static readonly IReadOnlyDictionary<HotkeyAction, HotkeyBinding> Defaults = new Dictionary<HotkeyAction, HotkeyBinding>
    {
        [HotkeyAction.ToggleLimiter] = new(HotkeyAction.ToggleLimiter, HotkeyBinding.Control | HotkeyBinding.Alt, 0x79),  // F10
        [HotkeyAction.IncreaseLimit] = new(HotkeyAction.IncreaseLimit, HotkeyBinding.Control | HotkeyBinding.Alt, 0x21),  // PgUp
        [HotkeyAction.DecreaseLimit] = new(HotkeyAction.DecreaseLimit, HotkeyBinding.Control | HotkeyBinding.Alt, 0x22),  // PgDn
        [HotkeyAction.ShowWindow] = new(HotkeyAction.ShowWindow, HotkeyBinding.Control | HotkeyBinding.Alt, 0x54),       // T
        [HotkeyAction.ToggleOverlay] = new(HotkeyAction.ToggleOverlay, HotkeyBinding.Control | HotkeyBinding.Alt, 0x4F)  // O
    };

    private readonly Dictionary<HotkeyAction, HotkeyBinding> _bindings = new();
    private readonly HashSet<HotkeyAction> _registered = new();
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _suspended;

    public HotkeyManager(IReadOnlyDictionary<string, string>? saved)
    {
        foreach (var (action, fallback) in Defaults)
        {
            // A saved empty string means "deliberately unassigned"; a missing entry means "use the default".
            if (saved is not null && saved.TryGetValue(action.ToString(), out var text))
            {
                _bindings[action] = HotkeyBinding.TryParse(action, text) ?? HotkeyBinding.Unassigned(action);
            }
            else
            {
                _bindings[action] = fallback;
            }
        }
    }

    public event Action<HotkeyAction>? Pressed;

    public IReadOnlyList<HotkeyBinding> Bindings => Defaults.Keys.Select(action => _bindings[action]).ToList();

    public HotkeyBinding Get(HotkeyAction action) => _bindings[action];

    public static string Describe(HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleLimiter => "Pause or resume the limiter",
        HotkeyAction.IncreaseLimit => "Raise the limit by 5 FPS",
        HotkeyAction.DecreaseLimit => "Lower the limit by 5 FPS",
        HotkeyAction.ToggleOverlay => "Show or hide the in-game overlay",
        _ => "Open ThermalScope"
    };

    /// <summary>Registers every assigned binding; returns the shortcuts another app already owns.</summary>
    public IReadOnlyList<string> Register(Window window)
    {
        _handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
        return RegisterAll();
    }

    /// <summary>Stops listening (while the settings UI records a new shortcut, so the old ones do not fire).</summary>
    public void Suspend()
    {
        _suspended = true;
        UnregisterAll();
    }

    public IReadOnlyList<string> Resume()
    {
        _suspended = false;
        return RegisterAll();
    }

    /// <summary>Assigns a new shortcut. Fails when it clashes with another action or another app owns it.</summary>
    public bool Rebind(HotkeyBinding binding, out string error)
    {
        error = string.Empty;
        if (binding.IsAssigned)
        {
            var clash = _bindings.Values.FirstOrDefault(other => other.Action != binding.Action && other.SameKeys(binding));
            if (clash is not null)
            {
                error = $"{binding.Text} is already used for \"{Describe(clash.Action)}\".";
                return false;
            }

            if (!CanRegister(binding))
            {
                error = $"{binding.Text} is taken by another app. Pick a different shortcut.";
                return false;
            }
        }

        var wasSuspended = _suspended;
        if (!wasSuspended)
        {
            Unregister(binding.Action);
        }

        _bindings[binding.Action] = binding;
        if (!wasSuspended)
        {
            Register(binding);
        }

        return true;
    }

    public IReadOnlyList<string> ResetToDefaults()
    {
        UnregisterAll();
        foreach (var (action, binding) in Defaults)
        {
            _bindings[action] = binding;
        }

        return _suspended ? Array.Empty<string>() : RegisterAll();
    }

    /// <summary>Settings representation: action name to shortcut text (empty = unassigned).</summary>
    public Dictionary<string, string> Export() =>
        _bindings.ToDictionary(pair => pair.Key.ToString(), pair => pair.Value.Text);

    private IReadOnlyList<string> RegisterAll()
    {
        var failed = new List<string>();
        foreach (var binding in _bindings.Values.Where(b => b.IsAssigned))
        {
            if (!Register(binding))
            {
                failed.Add(binding.Text);
            }
        }

        return failed;
    }

    private bool Register(HotkeyBinding binding)
    {
        if (_handle == IntPtr.Zero || !binding.IsAssigned)
        {
            return false;
        }

        var ok = RegisterHotKey(_handle, Id(binding.Action), binding.Modifiers | ModNoRepeat, binding.Key);
        if (ok)
        {
            _registered.Add(binding.Action);
        }

        return ok;
    }

    // Probes whether another app already owns the combination by registering it on a throwaway id.
    private bool CanRegister(HotkeyBinding binding)
    {
        if (_handle == IntPtr.Zero)
        {
            return true;
        }

        const int probeId = 0x7F00;
        if (!RegisterHotKey(_handle, probeId, binding.Modifiers | ModNoRepeat, binding.Key))
        {
            // Already registered by us for this very action (re-assigning the same keys) is fine.
            return _bindings.TryGetValue(binding.Action, out var current) && current.SameKeys(binding);
        }

        UnregisterHotKey(_handle, probeId);
        return true;
    }

    private void Unregister(HotkeyAction action)
    {
        if (_registered.Remove(action) && _handle != IntPtr.Zero)
        {
            UnregisterHotKey(_handle, Id(action));
        }
    }

    private void UnregisterAll()
    {
        foreach (var action in _registered.ToList())
        {
            Unregister(action);
        }
    }

    private static int Id(HotkeyAction action) => (int)action + 1;

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey)
        {
            var action = (HotkeyAction)(wParam.ToInt32() - 1);
            if (_registered.Contains(action))
            {
                Pressed?.Invoke(action);
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}
