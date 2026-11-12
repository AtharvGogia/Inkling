using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static Inkling.Native;

namespace Inkling.Ui;

/// <summary>Icon = the small "i_rdy" badge in the screen's top-right corner (config value "icon"); Bar = text bar by the cursor.</summary>
public enum IndicatorStyle { Icon, Bar }

/// <summary>
/// Shows capture state: the small "i_rdy" badge in the screen's top-right corner (default), or a text bar with
/// instructions near the caret. Neither window takes focus or receives clicks.
/// </summary>
internal sealed class StatusOverlay : IDisposable
{
    private readonly BadgeIndicator _icon = new();
    private readonly BarIndicator _bar = new();

    public IndicatorStyle Style { get; set; } = IndicatorStyle.Icon;

    /// <summary>Black badge for a dark theme, white for a light one.</summary>
    public bool Dark { get => _icon.Dark; set => _icon.Dark = value; }

    /// <summary>Raised for result/error messages in badge mode, which has no room for text (tray shows a balloon).</summary>
    public event Action<string, bool>? Message;

    public StatusOverlay()
    {
        _ = _icon.Handle;
        _ = _bar.Handle;
    }

    public static IndicatorStyle Parse(string s) =>
        s.Equals("bar", StringComparison.OrdinalIgnoreCase) ? IndicatorStyle.Bar : IndicatorStyle.Icon;

    public void ShowListening(nint target, int chars)
    {
        if (Style == IndicatorStyle.Bar)
            _bar.Display(target, chars == 0 ? "Inkling ▸ type your request · Enter = ask · Esc = cancel" : $"Inkling ▸ {chars} chars · Enter = ask · Esc = cancel",
                InklingArt.Accent, 0);
        else if (chars == 0 || !_icon.Visible) _icon.Start(target, BadgeIndicator.Mode.Ready);
    }

    public void ShowThinking(nint target, string backend)
    {
        if (Style == IndicatorStyle.Bar) _bar.Display(target, $"Inkling ▸ asking {backend}… (Esc = cancel)", InklingArt.Thinking, 0);
        else { _bar.HideNow(); _icon.Start(target, BadgeIndicator.Mode.Thinking); }
    }

    public void ShowMessage(nint target, string text, bool isError, int autoHideMs)
    {
        if (Style == IndicatorStyle.Bar)
        {
            _bar.Display(target, "Inkling ▸ " + text, isError ? InklingArt.Error : InklingArt.Done, autoHideMs);
            return;
        }
        // An error shows "i_err" briefly; other notices just clear the badge.
        if (isError) _icon.Start(target, BadgeIndicator.Mode.Error);
        else _icon.Stop();
        // The badge has no room for text: anything worth reading goes to a tray notification. Routine
        // "Cancelled (...)" and "Not typed" notices don't.
        if (isError || !(text.StartsWith("Cancelled", StringComparison.Ordinal) || text == "Not typed")) Message?.Invoke(text, isError);
    }

    /// <summary>The answer is in: "i_fin" for a moment (badge), or the bar goes away.</summary>
    public void ShowDone(nint target)
    {
        if (Style == IndicatorStyle.Bar) _bar.HideNow();
        else { _bar.HideNow(); _icon.Start(target, BadgeIndicator.Mode.Done); }
    }

    /// <summary>A question that needs an answer (risky command): always the text bar, whatever the style, until hidden.</summary>
    public void ShowConfirm(nint target, string text)
    {
        _icon.Stop();
        _bar.Display(target, "Inkling ▸ " + text, InklingArt.Error, 0);
    }

    public void HideNow()
    {
        _icon.Stop();
        _bar.HideNow();
    }

    public void Dispose()
    {
        _icon.Dispose();
        _bar.Dispose();
    }

    /// <summary>Below the text caret when the app exposes one; otherwise bottom-centre of the target window.</summary>
    internal static Point AnchorPoint(nint target, int w, int h)
    {
        var tid = GetWindowThreadProcessId(target, out _);
        var gui = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        Point p;
        if (GetGUIThreadInfo(tid, ref gui) && gui.hwndCaret != 0)
        {
            var pt = new POINT { X = gui.rcCaret.Right, Y = gui.rcCaret.Bottom };
            ClientToScreen(gui.hwndCaret, ref pt);
            p = new Point(pt.X + 4, pt.Y + 4);
        }
        else if (target != 0 && GetWindowRect(target, out var r))
        {
            p = new Point((r.Left + r.Right - w) / 2, r.Bottom - h - 40);
        }
        else
        {
            GetCursorPos(out var c);
            p = new Point(c.X + 12, c.Y + 18);
        }
        var screen = Screen.FromPoint(p).WorkingArea;
        p.X = Math.Clamp(p.X, screen.Left, Math.Max(screen.Left, screen.Right - w));
        p.Y = Math.Clamp(p.Y, screen.Top, Math.Max(screen.Top, screen.Bottom - h));
        return p;
    }

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(nint hWnd, out RECT rect);

    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);

    internal const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;
    internal const uint SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
}

/// <summary>
/// A small badge in the top-right corner of the screen, in a terminal font, on black or white to match the theme:
/// "i_rdy" in orange while you type, "i_thnk" in blue while thinking, "i_fin" in green when the answer is in,
/// "i_err" in red on an error. Per-pixel alpha (UpdateLayeredWindow); never takes focus or clicks.
/// </summary>
internal sealed class BadgeIndicator : Form
{
    public enum Mode { Ready, Thinking, Done, Error }

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private Mode _mode;
    private long _startTick;
    private Size _size;
    private float _scale = 1f;

    /// <summary>Black badge (dark theme) or white (light).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Dark { get; set; } = true;

    public BadgeIndicator()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        _timer.Tick += (_, _) => Render();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= StatusOverlay.WS_EX_TOPMOST | StatusOverlay.WS_EX_TRANSPARENT | StatusOverlay.WS_EX_TOOLWINDOW
                | StatusOverlay.WS_EX_NOACTIVATE | StatusOverlay.WS_EX_LAYERED;
            return cp;
        }
    }

    /// <summary>Cascadia Mono (Windows Terminal's font) if installed, else Consolas.</summary>
    internal static Font MonoFont(float px)
    {
        foreach (var name in new[] { "Cascadia Mono", "Cascadia Code", "Consolas" })
        {
            var f = new Font(name, px, FontStyle.Bold, GraphicsUnit.Pixel);
            if (f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return f;
            f.Dispose();
        }
        return new Font(FontFamily.GenericMonospace, px, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    /// <summary>Shows the badge in the top-right corner of the screen the target window is on.</summary>
    public void Start(nint target, Mode mode)
    {
        _mode = mode;
        _startTick = Environment.TickCount64;
        var screen = target != 0 ? Screen.FromHandle(target) : Screen.PrimaryScreen!;
        _scale = DeviceDpi / 96f;
        // Sized for the longest state ("i_thnk"), so the badge never changes size.
        using (var font = MonoFont(12f * _scale))
        {
            var text = TextRenderer.MeasureText(Widest, font, Size.Empty, TextFormatFlags.NoPadding);
            _size = new Size(text.Width + (int)Math.Round(14 * _scale), text.Height + (int)Math.Round(6 * _scale));
        }
        int margin = (int)Math.Round(8 * _scale);
        var p = new Point(screen.WorkingArea.Right - margin - _size.Width, screen.WorkingArea.Top + margin);
        Bounds = new Rectangle(p, _size);
        Render();
        if (!Visible) Show();
        StatusOverlay.SetWindowPos(Handle, -1, p.X, p.Y, _size.Width, _size.Height, StatusOverlay.SWP_NOACTIVATE | StatusOverlay.SWP_SHOWWINDOW);
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        if (Visible) Hide();
    }

    internal const string Widest = "i_thnk";

    /// <summary>
    /// The badge text for a state at time <paramref name="t"/> (seconds); null when it should hide. The underscore
    /// blinks like a cursor: slowly while ready, faster while thinking.
    /// </summary>
    internal static string? TextAt(Mode mode, double t) => mode switch
    {
        Mode.Ready => (int)(t / 0.53) % 2 == 0 ? "i_rdy" : "i rdy",
        Mode.Thinking => (int)(t / 0.25) % 2 == 0 ? "i_thnk" : "i thnk",
        Mode.Done => t > 1.2 ? null : "i_fin",
        _ => t > 2.0 ? null : "i_err",
    };

    internal static Color ColorFor(Mode mode) => mode switch
    {
        Mode.Ready => InklingArt.Accent,
        Mode.Thinking => InklingArt.Thinking,
        Mode.Done => InklingArt.Done,
        _ => InklingArt.Error,
    };

    /// <summary>Draws the badge into a bitmap of <paramref name="size"/> (also used for the README GIFs).</summary>
    internal static void DrawBadge(Graphics g, Size size, float scale, string text, Color color, bool dark)
    {
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        float r = 4f * scale;
        using (var shape = RoundRect(0.5f, 0.5f, size.Width - 1.5f, size.Height - 1.5f, r))
        {
            using var bg = new SolidBrush(dark ? Color.FromArgb(240, 12, 12, 12) : Color.FromArgb(245, 255, 255, 255));
            g.FillPath(bg, shape);
            using var edge = new Pen(dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(60, 0, 0, 0), Math.Max(1f, scale * 0.75f));
            g.DrawPath(edge, shape);
        }
        using var font = MonoFont(12f * scale);
        using var brush = new SolidBrush(color);
        // Count trailing spaces so text of any length stays centred the same way.
        var fmt = new StringFormat(StringFormatFlags.MeasureTrailingSpaces) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, new RectangleF(0, 0, size.Width, size.Height), fmt);
    }

    private void Render()
    {
        if (_size.IsEmpty) return;
        double t = (Environment.TickCount64 - _startTick) / 1000.0;
        var text = TextAt(_mode, t);
        if (text == null) { Stop(); return; }
        using var bmp = new Bitmap(_size.Width, _size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            DrawBadge(g, _size, _scale, text, ColorFor(_mode), Dark);
        Push(bmp);
    }

    private static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private void Push(Bitmap bmp)
    {
        var screenDc = GetDC(0);
        var memDc = CreateCompatibleDC(screenDc);
        var hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memDc, hBmp);
        try
        {
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var dst = new POINT { X = Left, Y = Top };
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(0, screenDc);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(nint hwnd, nint hdcDst, ref POINT pptDst, ref SIZE psize, nint hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hWnd, nint hDC);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint hDC, nint hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint hObject);
}

/// <summary>The text bar: shows instructions and messages.</summary>
internal sealed class BarIndicator : Form
{
    private readonly System.Windows.Forms.Timer _hideTimer = new();
    private string _text = "";
    private Color _accent = InklingArt.Accent;

    public BarIndicator()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(28, 28, 32);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9f, FontStyle.Regular);
        DoubleBuffered = true;
        Opacity = 0.94;
        _hideTimer.Tick += (_, _) => HideNow();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= StatusOverlay.WS_EX_TOPMOST | StatusOverlay.WS_EX_TRANSPARENT | StatusOverlay.WS_EX_TOOLWINDOW
                | StatusOverlay.WS_EX_NOACTIVATE | StatusOverlay.WS_EX_LAYERED;
            return cp;
        }
    }

    public void HideNow()
    {
        _hideTimer.Stop();
        if (Visible) Hide();
    }

    public void Display(nint target, string text, Color accent, int autoHideMs)
    {
        _text = text;
        _accent = accent;
        var size = TextRenderer.MeasureText(_text, Font);
        var w = size.Width + 22;
        var h = size.Height + 10;
        var pos = StatusOverlay.AnchorPoint(target, w, h);
        StatusOverlay.SetWindowPos(Handle, -1, pos.X, pos.Y, w, h, StatusOverlay.SWP_NOACTIVATE | StatusOverlay.SWP_SHOWWINDOW);
        if (!Visible) Show();
        Invalidate();

        _hideTimer.Stop();
        if (autoHideMs > 0)
        {
            _hideTimer.Interval = autoHideMs;
            _hideTimer.Start();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var bar = new SolidBrush(_accent)) g.FillRectangle(bar, 0, 0, 4, Height);
        TextRenderer.DrawText(g, _text, Font, new Rectangle(12, 0, Width - 12, Height), ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hideTimer.Dispose();
        base.Dispose(disposing);
    }
}
