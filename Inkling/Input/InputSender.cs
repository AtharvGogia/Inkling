using System.Runtime.InteropServices;
using static Inkling.Native;

namespace Inkling.Input;

/// <summary>Injects keystrokes with SendInput. Everything is tagged with <see cref="Native.InjectedMarker"/>.</summary>
internal static class InputSender
{
    private const ushort VK_BACK = 0x08;
    private const ushort VK_RETURN = 0x0D;
    /// <summary>Unassigned virtual key, used to stop a lone Alt/Win release from opening a menu.</summary>
    private const ushort VK_MASK = 0xE8;

    private static INPUT Key(ushort vk, ushort scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = InjectedMarker } },
    };

    private static void Send(IReadOnlyCollection<INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        var arr = inputs.ToArray();
        var sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
        if (sent != arr.Length) Log.Write($"SendInput sent {sent}/{arr.Length} (error {Marshal.GetLastWin32Error()}). Target app may be elevated.");
    }

    /// <summary>
    /// After swallowing a hotkey that includes Alt or Win, the app would otherwise see a bare
    /// Alt/Win press+release and open its menu bar / Start menu.
    /// </summary>
    public static void SendMaskKey() => Send([Key(VK_MASK, 0, 0), Key(VK_MASK, 0, KEYEVENTF_KEYUP)]);

    /// <summary>Ctrl+key as one atomic batch (nothing the user types can land in between).</summary>
    public static void SendCtrlKey(ushort vk)
    {
        ushort ctrlScan = (ushort)MapVirtualKeyEx(VK_CONTROL, MAPVK_VK_TO_VSC, 0);
        ushort scan = (ushort)MapVirtualKeyEx(vk, MAPVK_VK_TO_VSC, 0);
        Send([Key(VK_CONTROL, ctrlScan, 0), Key(vk, scan, 0), Key(vk, scan, KEYEVENTF_KEYUP), Key(VK_CONTROL, ctrlScan, KEYEVENTF_KEYUP)]);
    }

    /// <summary>The text exactly as <see cref="TypeTextAsync"/> puts it on screen.</summary>
    public static string AsTyped(string text, bool newlinesAsEnter)
    {
        var s = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ');
        return newlinesAsEnter ? s : s.Replace("\n", "");
    }

    /// <summary>Paced backspaces (some apps drop keys sent in a burst).</summary>
    public static async Task BackspaceAsync(int count, int delayMs)
    {
        timeBeginPeriod(1);
        try
        {
            for (int i = 0; i < count; i++)
            {
                Send([Key(VK_BACK, 0, 0), Key(VK_BACK, 0, KEYEVENTF_KEYUP)]);
                await Task.Delay(Math.Max(1, delayMs));
            }
        }
        finally { timeEndPeriod(1); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern short VkKeyScanEx(char ch, nint dwhkl);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, nint dwhkl);

    private const uint MAPVK_VK_TO_VSC = 0, MAPVK_VK_TO_CHAR = 2;

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    /// <summary>
    /// Types text into the window, one paced keystroke at a time. Some apps (e.g. the WinUI Notepad) read
    /// Shift and Unicode-packet state when they *process* a key, not when it was sent, so a burst turns
    /// "Jupiter" into "rrrrrrr" or "Everest" into "everest". Characters on the target's layout are sent as
    /// real key presses (Shift held across the key); others (emoji, AltGr, dead-key chars) as Unicode packets.
    /// </summary>
    public static async Task TypeTextAsync(string text, bool newlinesAsEnter, int delayMs, nint targetHwnd)
    {
        var layout = GetKeyboardLayout(GetWindowThreadProcessId(targetHwnd, out _));
        bool capsOn = (GetKeyState(VK_CAPITAL) & 1) != 0;
        ushort shiftScan = (ushort)MapVirtualKeyEx(VK_SHIFT, MAPVK_VK_TO_VSC, layout);
        int pace = Math.Max(1, delayMs);
        bool shiftDown = false;

        timeBeginPeriod(1); // make Task.Delay(5) ≈ 5 ms instead of 15.6 ms
        try
        {
            var chars = AsTyped(text, newlinesAsEnter);
            for (int i = 0; i < chars.Length; i++)
            {
                char ch = chars[i];

                ushort vk = VK_RETURN;
                bool shift = false;
                bool real = ch == '\n' || TryKeyFor(ch, layout, capsOn, out vk, out shift);

                // Hold/release Shift only when it changes, with a pause so the app sees the new state.
                if (shiftDown != (real && shift))
                {
                    shiftDown = real && shift;
                    Send([Key(VK_SHIFT, shiftScan, shiftDown ? 0 : KEYEVENTF_KEYUP)]);
                    await Task.Delay(pace);
                }

                if (real)
                {
                    ushort scan = (ushort)MapVirtualKeyEx(vk, MAPVK_VK_TO_VSC, layout);
                    Send([Key(vk, scan, 0), Key(vk, scan, KEYEVENTF_KEYUP)]);
                }
                else if (char.IsHighSurrogate(ch) && i + 1 < chars.Length)
                {
                    char lo = chars[++i]; // both halves of an emoji in one call
                    Send([Key(0, ch, KEYEVENTF_UNICODE), Key(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP),
                          Key(0, lo, KEYEVENTF_UNICODE), Key(0, lo, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP)]);
                }
                else
                {
                    Send([Key(0, ch, KEYEVENTF_UNICODE), Key(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP)]);
                }
                await Task.Delay(pace);
            }
        }
        finally
        {
            if (shiftDown) Send([Key(VK_SHIFT, shiftScan, KEYEVENTF_KEYUP)]);
            timeEndPeriod(1);
        }
    }

    /// <summary>Finds a plain or Shift key for <paramref name="ch"/> on the layout; false for AltGr/dead keys/missing.</summary>
    private static bool TryKeyFor(char ch, nint layout, bool capsOn, out ushort vk, out bool shift)
    {
        vk = 0; shift = false;
        if (char.IsSurrogate(ch) || char.IsControl(ch)) return false;
        short res = VkKeyScanEx(ch, layout);
        if (res == -1) return false;
        int mods = (res >> 8) & 0xFF;
        if ((mods & ~1) != 0) return false;                  // needs Ctrl/Alt (AltGr): use a packet
        vk = (ushort)(res & 0xFF);
        if ((MapVirtualKeyEx(vk, MAPVK_VK_TO_CHAR, layout) & 0x80000000) != 0) return false; // dead key
        shift = (mods & 1) != 0;
        // Caps Lock flips Shift for letters.
        if (capsOn && char.IsLetter(ch) && char.ToUpperInvariant(ch) != char.ToLowerInvariant(ch)) shift = !shift;
        return true;
    }


    /// <summary>Waits until the user lets go of modifiers, so injected Backspace isn't read as Ctrl+Backspace etc.</summary>
    public static async Task WaitForModifiersReleasedAsync(int timeoutMs = 3000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until &&
               (IsDown(VK_SHIFT) || IsDown(VK_CONTROL) || IsDown(VK_MENU) || IsDown(VK_LWIN) || IsDown(VK_RWIN)))
        {
            await Task.Delay(20);
        }
    }
}
