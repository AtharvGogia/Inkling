using System.Runtime.InteropServices;
using static Inkling.Native;

namespace Inkling.Input;

/// <summary>
/// Global low-level keyboard + mouse hooks. Must be created on a thread that pumps messages
/// (the WinForms UI thread). Handlers run on that thread and must return quickly, or Windows
/// silently drops the hook.
/// </summary>
internal sealed class Hooks : IDisposable
{
    /// <summary>Return true to swallow the key (the focused app never sees it).</summary>
    public Func<KBDLLHOOKSTRUCT, bool>? KeyDown;
    public Action? MouseDown;

    private readonly HookProc _kbProc;
    private readonly HookProc _mouseProc;
    private nint _kbHook;
    private nint _mouseHook;

    public Hooks()
    {
        // Keep delegates in fields so the GC never collects them while Windows still calls them.
        _kbProc = KeyboardCallback;
        _mouseProc = MouseCallback;
        var hMod = GetModuleHandle(null);
        _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);
        if (_kbHook == 0) throw new InvalidOperationException($"Keyboard hook failed (error {Marshal.GetLastWin32Error()}).");
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
        if (_mouseHook == 0) Log.Write($"Mouse hook failed (error {Marshal.GetLastWin32Error()}); clicks won't cancel capture.");
    }

    private nint KeyboardCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // Ignore what we inject ourselves (backspaces, typed answer).
            if (info.dwExtraInfo != InjectedMarker)
            {
                try
                {
                    if (KeyDown?.Invoke(info) == true) return 1;
                }
                catch (Exception ex) { Log.Write("Key handler error: " + ex.Message); }
            }
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private nint MouseCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && (wParam == WM_LBUTTONDOWN || wParam == WM_RBUTTONDOWN || wParam == WM_MBUTTONDOWN))
        {
            try { MouseDown?.Invoke(); }
            catch (Exception ex) { Log.Write("Mouse handler error: " + ex.Message); }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_kbHook != 0) { UnhookWindowsHookEx(_kbHook); _kbHook = 0; }
        if (_mouseHook != 0) { UnhookWindowsHookEx(_mouseHook); _mouseHook = 0; }
    }
}
