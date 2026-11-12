using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using static Inkling.Native;

namespace Inkling.Ui;

public enum IndicatorStyle { Icon, Bar }

/// <summary>
/// Shows capture state near the caret, either as a small animated Inkling mark (default) or as a
/// text bar with instructions. Neither window takes focus or receives clicks.
/// </summary>
internal sealed class StatusOverlay : IDisposable
{
    private readonly IconIndicator _icon = new();
    private readonly BarIndicator _bar = new();

    public IndicatorStyle Style { get; set; } = IndicatorStyle.Icon;

    /// <summary>Raised for result/error messages in icon mode, which has no room for text (tray shows a balloon).</summary>
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
                InklingArt.Violet, 0);
        else if (chars == 0 || !_icon.Visible) _icon.Start(target, IconIndicator.Mode.Listening);
    }

    public void ShowThinking(nint target, string backend)
    {
        if (Style == IndicatorStyle.Bar) _bar.Display(target, $"Inkling ▸ asking {backend}… (Esc = cancel)", InklingArt.Amber, 0);
        else _icon.Start(target, IconIndicator.Mode.Thinking);
    }

    public void ShowMessage(nint target, string text, bool isError, int autoHideMs)
    {
        if (Style == IndicatorStyle.Bar)
        {
            _bar.Display(target, "Inkling ▸ " + text, isError ? InklingArt.Red : InklingArt.Green, autoHideMs);
            return;
        }
        _icon.Start(target, isError ? IconIndicator.Mode.Error : IconIndicator.Mode.Done);
        // Short cancel notices don't need a balloon; errors and "copied to clipboard" do.
        if (isError || text.Contains("clipboard", StringComparison.OrdinalIgnoreCase)) Message?.Invoke(text, isError);
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

/// <summary>Small pill in the screen's top-left corner: the Inkling mark, plus ". . ." while thinking. Per-pixel alpha (UpdateLayeredWindow).</summary>
internal sealed class IconIndicator : Form
{
    public enum Mode { Listening, Thinking, Done, Error }

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private Mode _mode;
    private long _startTick;
    private int _size;

    public IconIndicator()
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

    private int _width;

    /// <summary>Shows the indicator in the top-left corner of the screen the target window is on.</summary>
    public void Start(nint target, Mode mode)
    {
        _mode = mode;
        _startTick = Environment.TickCount64;
        var screen = target != 0 ? Screen.FromHandle(target) : Screen.PrimaryScreen!;
        // Scale for the monitor the indicator sits on (DeviceDpi is the form's current monitor).
        double dpi = DeviceDpi / 96.0;
        _size = (int)Math.Round(22 * dpi);          // height of the pill
        _width = _size;                              // just the round light
        int margin = (int)Math.Round(8 * dpi);
        var p = new Point(screen.WorkingArea.Left + margin, screen.WorkingArea.Top + margin);
        Bounds = new Rectangle(p, new Size(_width, _size));
        Render();
        if (!Visible) Show();
        StatusOverlay.SetWindowPos(Handle, -1, p.X, p.Y, _width, _size, StatusOverlay.SWP_NOACTIVATE | StatusOverlay.SWP_SHOWWINDOW);
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        if (Visible) Hide();
    }

    private void Render()
    {
        double t = (Environment.TickCount64 - _startTick) / 1000.0;
        int s = _size;
        float scale = 1f;
        int alpha = 255;
        Color color;
        string dots = "";

        switch (_mode)
        {
            case Mode.Listening: // active: gentle breathing
                color = InklingArt.Violet;
                double b = (Math.Sin(t * Math.PI * 2 / 1.4) + 1) / 2;
                scale = 0.88f + 0.12f * (float)b;
                alpha = 190 + (int)(65 * b);
                break;
            case Mode.Thinking: // amber, quick pulse (the ". . ." animation runs in the text field itself)
                color = InklingArt.Amber;
                double q = (Math.Sin(t * Math.PI * 2 / 0.45) + 1) / 2;
                alpha = 150 + (int)(105 * q);
                break;
            case Mode.Done: // green, then fade out
                color = InklingArt.Green;
                if (t > 0.9) { Stop(); return; }
                alpha = t < 0.5 ? 255 : (int)(255 * (1 - (t - 0.5) / 0.4));
                break;
            default: // Error: three red blinks
                color = InklingArt.Red;
                if (t > 1.5) { Stop(); return; }
                alpha = ((int)(t / 0.25) % 2 == 0) ? 255 : 60;
                break;
        }

        using var bmp = new Bitmap(_width, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // Dark pill behind the icon (and the dots), so it reads on any wallpaper.
            float pillW = dots.Length == 0 ? s : _width;
            using (var pill = RoundedPill(0, 0, pillW - 1, s - 1))
            using (var bg = new SolidBrush(Color.FromArgb(Math.Min(alpha, 215), 24, 24, 28)))
                g.FillPath(bg, pill);

            float pad = s * 0.12f;
            float d = (s - 2 * pad) * scale;
            InklingArt.DrawMark(g, new RectangleF((s - d) / 2f, (s - d) / 2f, d, d), color, 1f, alpha);

            if (dots.Length > 0)
            {
                using var font = new Font("Segoe UI", s * 0.55f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var brush = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
                var fmt = new StringFormat { LineAlignment = StringAlignment.Center };
                g.DrawString(dots, font, brush, new RectangleF(s + s * 0.05f, -s * 0.12f, _width - s, s), fmt);
            }
        }
        Push(bmp);
    }

    private static GraphicsPath RoundedPill(float x, float y, float w, float h)
    {
        var p = new GraphicsPath();
        float r = h;
        if (w <= h) { p.AddEllipse(x, y, h, h); return p; }
        p.AddArc(x, y, r, r, 90, 180);
        p.AddArc(x + w - r, y, r, r, 270, 180);
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
    private Color _accent = InklingArt.Violet;

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
