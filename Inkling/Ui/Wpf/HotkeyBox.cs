using System.Runtime.InteropServices;
using Inkling.Input;
using Key = System.Windows.Input.Key;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Keyboard = System.Windows.Input.Keyboard;
using KeyInterop = System.Windows.Input.KeyInterop;
using ModifierKeys = System.Windows.Input.ModifierKeys;
using WpfTextBox = System.Windows.Controls.TextBox;
using RoutedEventArgs = System.Windows.RoutedEventArgs;

namespace Inkling.Ui.Wpf;

/// <summary>
/// A box that records a shortcut: click it and press the keys. Esc keeps the old one; Backspace or
/// Delete clears it (when <see cref="AllowEmpty"/>). Reports conflicts through <see cref="Problem"/>.
/// </summary>
internal sealed class HotkeyBox : WpfTextBox
{
    private string _value = "";
    private string _before = "";

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = System.Windows.Input.Cursors.Hand;
        MinWidth = 200;
        // Read-only text boxes don't follow the dark theme on their own: use the theme's control colours.
        SetResourceReference(BackgroundProperty, "ControlFillColorDefaultBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        SetResourceReference(BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        Padding = new System.Windows.Thickness(8, 5, 8, 5);
        GotKeyboardFocus += (_, _) => { _before = _value; Text = "Press a shortcut…"; RecordingChanged?.Invoke(true); };
        LostKeyboardFocus += (_, _) => { Text = Display(_value); RecordingChanged?.Invoke(false); };
    }

    /// <summary>True while the box is waiting for keys: Inkling's own hotkeys must be paused then.</summary>
    public event Action<bool>? RecordingChanged;
    /// <summary>Raised with the new value after a successful recording.</summary>
    public event Action<string>? Changed;

    public bool AllowEmpty { get; set; }

    public string Value
    {
        get => _value;
        set { _value = value ?? ""; Text = IsKeyboardFocused ? "Press a shortcut…" : Display(_value); }
    }

    private string Display(string v) => v.Length == 0 ? "Off" : v.Replace("Return", "Enter");

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            Text = ModText(mods) + "…";
            return;
        }
        if (mods == ModifierKeys.None && key == Key.Escape) { Finish(_before); return; }
        if (mods == ModifierKeys.None && key is Key.Back or Key.Delete)
        {
            if (AllowEmpty) Finish(""); else Finish(_before);
            return;
        }
        if ((mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
        {
            Text = "Use Ctrl, Alt or Win with a key";
            return;
        }
        var keys = (Keys)KeyInterop.VirtualKeyFromKey(key);
        var text = ModText(mods) + Hotkey.KeyName(keys);
        Finish(text);
        Changed?.Invoke(text);
    }

    private void Finish(string value)
    {
        _value = value;
        // Move focus away so the next keys go back to normal (and Inkling's hotkeys resume).
        System.Windows.Input.Keyboard.ClearFocus();
        System.Windows.Input.FocusManager.SetFocusedElement(System.Windows.Input.FocusManager.GetFocusScope(this), null);
        Text = Display(_value);
        RecordingChanged?.Invoke(false);
    }

    private static string ModText(ModifierKeys m) =>
        ((m & ModifierKeys.Control) != 0 ? "Ctrl+" : "") + ((m & ModifierKeys.Alt) != 0 ? "Alt+" : "") +
        ((m & ModifierKeys.Shift) != 0 ? "Shift+" : "") + ((m & ModifierKeys.Windows) != 0 ? "Win+" : "");

    protected override void OnMouseDoubleClick(System.Windows.Input.MouseButtonEventArgs e) => e.Handled = true;

    // ---- Conflict checks ------------------------------------------------------------------------

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hWnd, int id);
    private const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    private static readonly Dictionary<string, string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl+Alt+Delete"] = "Windows security screen",
        ["Win+L"] = "locks Windows",
        ["Alt+Tab"] = "switches windows",
        ["Alt+F4"] = "closes windows",
        ["Ctrl+Shift+Escape"] = "Task Manager",
        ["Win+D"] = "shows the desktop",
        ["Win+E"] = "File Explorer",
        ["Win+R"] = "Run",
        ["Win+V"] = "clipboard history",
        ["Win+Space"] = "switches keyboard layout",
        ["Ctrl+C"] = "copy", ["Ctrl+V"] = "paste", ["Ctrl+X"] = "cut", ["Ctrl+Z"] = "undo", ["Ctrl+Y"] = "redo",
        ["Ctrl+A"] = "select all", ["Ctrl+S"] = "save", ["Ctrl+F"] = "find", ["Ctrl+W"] = "close tab", ["Ctrl+T"] = "new tab",
        ["Ctrl+Return"] = "send / new line in many apps", ["Ctrl+Space"] = "autocomplete in editors",
    };

    /// <summary>
    /// Why this shortcut is a poor choice, or null if it looks fine. Checks Windows/app shortcuts and,
    /// with RegisterHotKey, whether another program already owns it (Inkling itself uses a keyboard hook, not RegisterHotKey).
    /// </summary>
    public static string? Problem(string text)
    {
        if (text.Length == 0) return null;
        Hotkey hk;
        try { hk = Hotkey.Parse(text); }
        catch (FormatException ex) { return ex.Message; }
        if (Reserved.TryGetValue(hk.ToString(), out var what)) return $"{Pretty(hk.ToString())} is already {what}.";
        uint mods = MOD_NOREPEAT | (hk.Ctrl ? MOD_CONTROL : 0) | (hk.Alt ? MOD_ALT : 0) | (hk.Shift ? MOD_SHIFT : 0) | (hk.Win ? MOD_WIN : 0);
        const int id = 0x1A1A;
        if (!RegisterHotKey(0, id, mods, (uint)hk.Key)) return $"Another program already uses {Pretty(hk.ToString())}.";
        UnregisterHotKey(0, id);
        return null;
    }

    public static string Pretty(string hotkey) => hotkey.Replace("Return", "Enter");
}
