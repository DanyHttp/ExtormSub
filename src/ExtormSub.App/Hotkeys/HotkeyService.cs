using System.Windows.Input;
using System.Windows.Interop;
using ExtormSub.App.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ExtormSub.App.Hotkeys;

public readonly record struct HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    /// <summary>Parses "Ctrl+Alt+S". A gesture needs at least one modifier so it cannot swallow normal typing.</summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = ModifierKeys.None;
        Key key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win" or "windows": mods |= ModifierKeys.Windows; break;
                default:
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (!Enum.TryParse(name, true, out key)) return false;
                    break;
            }
        }
        if (key == Key.None || mods == ModifierKeys.None) return false;
        gesture = new HotkeyGesture(mods, key);
        return true;
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        _ => key.ToString(),
    };
}

public enum HotkeyAction { ToggleListening, ToggleLock, ToggleVisibility, EmergencyUnlock }

/// <summary>
/// System-wide hotkeys via RegisterHotKey on a message-only window. Registration failures (another app
/// owns the combination) are reported, never thrown. Must be used from the UI thread.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly ILogger _log;
    private readonly Dictionary<int, HotkeyAction> _registered = new();

    public HotkeyService(ILogger<HotkeyService> log)
    {
        _log = log;
        _source = new HwndSource(new HwndSourceParameters("ExtormSub.Hotkeys") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
        _source.AddHook(WndProc);
    }

    public event Action<HotkeyAction>? Pressed;

    /// <summary>Replaces all registrations. Returns the actions whose gesture is invalid or already taken.</summary>
    public IReadOnlyDictionary<HotkeyAction, string> Apply(IReadOnlyDictionary<HotkeyAction, string> bindings)
    {
        foreach (var id in _registered.Keys) Native.UnregisterHotKey(_source.Handle, id);
        _registered.Clear();

        var failures = new Dictionary<HotkeyAction, string>();
        int nextId = 0x5100;
        foreach (var (action, text) in bindings)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!HotkeyGesture.TryParse(text, out var g))
            {
                failures[action] = $"\"{text}\" is not a valid shortcut";
                continue;
            }
            int id = nextId++;
            uint mods = Native.MOD_NOREPEAT
                | (g.Modifiers.HasFlag(ModifierKeys.Control) ? Native.MOD_CONTROL : 0)
                | (g.Modifiers.HasFlag(ModifierKeys.Alt) ? Native.MOD_ALT : 0)
                | (g.Modifiers.HasFlag(ModifierKeys.Shift) ? Native.MOD_SHIFT : 0)
                | (g.Modifiers.HasFlag(ModifierKeys.Windows) ? Native.MOD_WIN : 0);
            if (Native.RegisterHotKey(_source.Handle, id, mods, (uint)KeyInterop.VirtualKeyFromKey(g.Key)))
            {
                _registered[id] = action;
            }
            else
            {
                failures[action] = $"{g} is already used by another application";
                _log.LogWarning("Hotkey {Gesture} for {Action} could not be registered", g, action);
            }
        }
        return failures;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Pressed?.Invoke(action);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _registered.Keys) Native.UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
