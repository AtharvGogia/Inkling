using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using static Inkling.Native;

namespace Inkling.Ui;

/// <summary>
/// Finds the text caret on screen, so the bar can sit next to it. Many apps (Windows 11 Notepad, Windows Terminal,
/// browsers, Electron apps) don't use a classic Win32 caret, so this tries, in order: the Win32 caret, the MSAA caret
/// object (Chromium and most Win32 controls), and UI Automation's text selection (WinUI, Terminal, Office).
/// </summary>
internal static class CaretLocator
{
    /// <summary>How long a lookup may take before the bar falls back to the mouse pointer.</summary>
    public const int TimeoutMs = 300;

    /// <summary>The classic Win32 caret, in screen pixels. Cheap and never blocks, so it's safe on the UI thread.</summary>
    public static Rectangle? Win32(nint target)
    {
        if (target == 0) return null;
        var gui = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(GetWindowThreadProcessId(target, out _), ref gui) || gui.hwndCaret == 0) return null;
        var tl = new POINT { X = gui.rcCaret.Left, Y = gui.rcCaret.Top };
        var br = new POINT { X = gui.rcCaret.Right, Y = gui.rcCaret.Bottom };
        ClientToScreen(gui.hwndCaret, ref tl);
        ClientToScreen(gui.hwndCaret, ref br);
        return Usable(Rectangle.FromLTRB(tl.X, tl.Y, br.X, br.Y));
    }

    /// <summary>All three methods; the slow ones run on a pool thread and give up after <see cref="TimeoutMs"/>.</summary>
    public static async Task<Rectangle?> FindAsync(nint target)
    {
        if (Win32(target) is { } quick) return quick;
        var work = Task.Run(() => Msaa(target) ?? Uia());
        return await Task.WhenAny(work, Task.Delay(TimeoutMs)).ConfigureAwait(false) == work ? work.Result : null;
    }

    /// <summary>Starts UI Automation's client side, whose first call takes a while, so the first bar isn't late.</summary>
    public static void Warm() => Task.Run(() => { try { _ = AutomationElement.RootElement; } catch { } });

    /// <summary>--caret-test: once a second, what each method finds in the foreground window. Sends no input.</summary>
    internal static int SelfTest(int seconds)
    {
        Console.WriteLine("Click into a text field in any app; the caret is looked up there once a second.");
        for (int i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);
            var fg = GetForegroundWindow();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var w = Win32(fg);
            var m = Msaa(fg);
            var u = Uia();
            GetWindowThreadProcessId(fg, out var pid);
            string name;
            try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { name = "?"; }
            Console.WriteLine($"{name,-20} win32={Show(w),-22} msaa={Show(m),-22} uia={Show(u),-22} {sw.ElapsedMilliseconds} ms");
        }
        return 0;
        static string Show(Rectangle? r) => r is { } x ? $"{x.X},{x.Y} h{x.Height}" : "-";
    }

    private static Rectangle? Msaa(nint target)
    {
        try
        {
            var gui = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            GetGUIThreadInfo(GetWindowThreadProcessId(target, out _), ref gui);
            var hwnd = gui.hwndFocus != 0 ? gui.hwndFocus : target;
            var iid = IID_IAccessible;
            if (AccessibleObjectFromWindow(hwnd, OBJID_CARET, ref iid, out var obj) != 0 || obj is not Accessibility.IAccessible acc) return null;
            acc.accLocation(out int l, out int t, out int w, out int h, 0);
            return Usable(new Rectangle(l, t, Math.Max(w, 1), h));
        }
        catch { return null; }
    }

    private static Rectangle? Uia()
    {
        try
        {
            var el = AutomationElement.FocusedElement;
            if (el == null || !el.TryGetCurrentPattern(TextPattern.Pattern, out var p)) return null;
            var sel = ((TextPattern)p).GetSelection();
            if (sel.Length == 0) return null;
            var range = sel[0];
            // A caret is an empty range, which most providers give no rectangle for: measure the character after
            // it, or, at the end of the text, the one before it (and use its right edge).
            var rects = range.GetBoundingRectangles();
            if (rects.Length > 0) return Usable(ToRect(rects[^1], atEnd: true));
            var next = range.Clone();
            next.ExpandToEnclosingUnit(TextUnit.Character);
            rects = next.GetBoundingRectangles();
            if (rects.Length > 0) return Usable(ToRect(rects[0], atEnd: false));
            var prev = range.Clone();
            if (prev.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1) != 0)
            {
                rects = prev.GetBoundingRectangles();
                if (rects.Length > 0) return Usable(ToRect(rects[^1], atEnd: true));
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>A 1-pixel-wide caret at the left (or right) edge of a text rectangle.</summary>
    private static Rectangle ToRect(System.Windows.Rect r, bool atEnd) =>
        new((int)Math.Round(atEnd ? r.Right : r.Left), (int)Math.Round(r.Top), 1, (int)Math.Round(r.Height));

    /// <summary>Apps report "no caret" in odd ways (all zeros, zero height, off screen).</summary>
    private static Rectangle? Usable(Rectangle r) =>
        r.Height <= 0 || (r.X == 0 && r.Y == 0) || !Screen.AllScreens.Any(s => s.Bounds.IntersectsWith(new Rectangle(r.X, r.Y, Math.Max(r.Width, 1), r.Height)))
            ? null : r;

    private const uint OBJID_CARET = 0xFFFFFFF8;
    private static readonly Guid IID_IAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(nint hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);
}
