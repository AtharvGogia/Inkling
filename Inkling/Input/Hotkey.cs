using static Inkling.Native;

namespace Inkling.Input;

/// <summary>A parsed hotkey such as "Ctrl+Alt+Space" or "Win+Shift+A".</summary>
internal sealed record Hotkey(Keys Key, bool Ctrl, bool Alt, bool Shift, bool Win)
{
    public static Hotkey Parse(string text)
    {
        bool ctrl = false, alt = false, shift = false, win = false;
        Keys? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win" or "windows": win = true; break;
                default:
                    if (Enum.TryParse<Keys>(raw, ignoreCase: true, out var k)) key = k;
                    else if (raw.Length == 1 && char.IsDigit(raw[0])) key = Keys.D0 + (raw[0] - '0');
                    else throw new FormatException($"Unknown key '{raw}' in hotkey '{text}'.");
                    break;
            }
        }
        if (key is null) throw new FormatException($"Hotkey '{text}' has no main key.");
        if (!ctrl && !alt && !win) throw new FormatException($"Hotkey '{text}' needs Ctrl, Alt or Win so normal typing can't trigger it.");
        return new Hotkey(key.Value, ctrl, alt, shift, win);
    }

    public bool Matches(uint vk) =>
        vk == (uint)Key
        && Ctrl == IsDown(VK_CONTROL)
        && Alt == IsDown(VK_MENU)
        && Shift == IsDown(VK_SHIFT)
        && Win == (IsDown(VK_LWIN) || IsDown(VK_RWIN));

    public override string ToString() =>
        string.Join("+", new[] { Ctrl ? "Ctrl" : null, Alt ? "Alt" : null, Shift ? "Shift" : null, Win ? "Win" : null, Key.ToString() }
            .Where(s => s != null));
}
