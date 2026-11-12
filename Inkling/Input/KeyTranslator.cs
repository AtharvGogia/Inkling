using System.Text;
using static Inkling.Native;

namespace Inkling.Input;

/// <summary>Turns a low-level key event into the character(s) it types, using the foreground window's layout.</summary>
internal static class KeyTranslator
{
    /// <summary>ToUnicodeEx flag (Win10 1607+): don't change the keyboard state, so dead keys keep working in the app.</summary>
    private const uint DontChangeKeyboardState = 0x4;

    public static string Translate(KBDLLHOOKSTRUCT info, nint foregroundHwnd)
    {
        var state = new byte[256];
        void Set(int vk) { if (IsDown(vk)) state[vk] = 0x80; }
        Set(VK_SHIFT); Set(VK_LSHIFT); Set(VK_RSHIFT);
        Set(VK_CONTROL); Set(VK_LCONTROL); Set(VK_RCONTROL);
        Set(VK_MENU); Set(VK_LMENU); Set(VK_RMENU);
        if ((GetKeyState(VK_CAPITAL) & 1) != 0) state[VK_CAPITAL] = 0x01;

        var thread = GetWindowThreadProcessId(foregroundHwnd, out _);
        var layout = GetKeyboardLayout(thread);
        var sb = new StringBuilder(8);
        int n = ToUnicodeEx(info.vkCode, info.scanCode, state, sb, sb.Capacity, DontChangeKeyboardState, layout);
        if (n <= 0) return ""; // no character, or a dead key waiting for the next press
        var s = sb.ToString(0, Math.Min(n, sb.Length));
        // Control characters (Ctrl+letter, Enter, Tab, Esc, Backspace) are not text.
        return s.Any(char.IsControl) ? "" : s;
    }
}
