using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;

namespace Inkling.Ui;

/// <summary>
/// The Inkling logo: an italic serif "inkling" whose i has a tilted orange square for a dot, ending in an orange
/// underscore cursor. The mark is the same i + dot + underscore on its own. Everything is drawn from Georgia
/// (on every Windows PC), so the icon, the tray light, the windows and the README art all match.
/// </summary>
internal static class InklingArt
{
    /// <summary>Inkling's colour (#FF7D00): the dot, the cursor, the light while you type, the Settings controls.</summary>
    public static readonly Color Accent = Color.FromArgb(0xFF, 0x7D, 0x00);
    /// <summary>
    /// Thinking (blue #4D9BFF), done (green #2DBE6C) and error (red #FF5A5F): picked at about the same lightness
    /// as the orange, so the badge's states read as one set and stay legible on black and on white.
    /// </summary>
    public static readonly Color Thinking = Color.FromArgb(0x4D, 0x9B, 0xFF);
    public static readonly Color Done = Color.FromArgb(0x2D, 0xBE, 0x6C);
    public static readonly Color Error = Color.FromArgb(0xFF, 0x5A, 0x5F);
    /// <summary>The icon tile and the ink of the letters.</summary>
    public static readonly Color Paper = Color.FromArgb(0xFB, 0xF8, 0xF4);
    public static readonly Color PaperEdge = Color.FromArgb(0xE6, 0xDE, 0xD3);
    public static readonly Color Ink = Color.FromArgb(0x1F, 0x1F, 0x1F);

    private static FontFamily Serif()
    {
        foreach (var name in new[] { "Georgia", "Cambria", "Times New Roman" })
            try { return new FontFamily(name); } catch (ArgumentException) { }
        return FontFamily.GenericSerif;
    }

    /// <summary>Letters and accent parts of the logo, at em size 100, before fitting into a box.</summary>
    private sealed class Parts : IDisposable
    {
        public readonly GraphicsPath Letters = new(FillMode.Winding);
        public readonly GraphicsPath Accent = new(FillMode.Winding);
        public RectangleF Bounds
        {
            get
            {
                var a = Letters.GetBounds();
                var b = Accent.GetBounds();
                return RectangleF.Union(a, b);
            }
        }
        public void Transform(Matrix m) { Letters.Transform(m); Accent.Transform(m); }
        public void Dispose() { Letters.Dispose(); Accent.Dispose(); }
    }

    /// <summary>
    /// The letters as written ("ınkling", or just "ı" for the mark, with a dotless i), the tilted square dot above
    /// the first i, and the underscore after the last letter.
    /// </summary>
    private static Parts Build(string text, bool bold = false)
    {
        const float em = 100f;
        var p = new Parts();
        using var family = Serif();
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        var style = (int)(bold ? FontStyle.Italic | FontStyle.Bold : FontStyle.Italic);
        p.Letters.AddString(text, family, style, em, PointF.Empty, fmt);

        // Where the first i sits: its own outline, drawn at the same origin.
        using var first = new GraphicsPath();
        first.AddString(text[..1], family, style, em, PointF.Empty, fmt);
        var i = first.GetBounds();
        var all = p.Letters.GetBounds();

        // The dot: a square tilted 12°, centred above the top of the italic stem (which leans right).
        float side = em * (bold ? 0.2f : 0.17f);
        var dotCentre = new PointF(i.Left + i.Width * 0.72f, i.Top - side * 0.95f);
        using (var dot = new GraphicsPath())
        {
            dot.AddRectangle(new RectangleF(dotCentre.X - side / 2, dotCentre.Y - side / 2, side, side));
            using var rot = new Matrix();
            rot.RotateAt(12f, dotCentre);
            dot.Transform(rot);
            p.Accent.AddPath(dot, false);
        }

        // The cursor: an underscore on the baseline, just after the text.
        float baseline = all.Bottom - (text.EndsWith('g') ? em * 0.21f : 0f); // "g" hangs below the baseline
        float thick = em * (bold ? 0.1f : 0.075f);
        p.Accent.AddRectangle(new RectangleF(all.Right + em * 0.06f, baseline - thick, em * (text.Length == 1 ? 0.48f : 0.42f), thick));
        return p;
    }

    /// <summary>Scales and centres the parts into <paramref name="r"/> (times <paramref name="scale"/>).</summary>
    private static void Fit(Parts p, RectangleF r, float scale = 1f)
    {
        var b = p.Bounds;
        float k = Math.Min(r.Width / b.Width, r.Height / b.Height) * scale;
        using var m = new Matrix();
        m.Translate(r.X + (r.Width - b.Width * k) / 2f, r.Y + (r.Height - b.Height * k) / 2f);
        m.Scale(k, k);
        m.Translate(-b.X, -b.Y);
        p.Transform(m);
    }

    /// <summary>
    /// The mark (i, dot, underscore) inside <paramref name="r"/>: letters in <paramref name="ink"/> (white by default,
    /// for the dark tray light), dot and cursor in <paramref name="color"/>. <paramref name="scale"/> 1 fills the box.
    /// </summary>
    public static void DrawMark(Graphics g, RectangleF r, Color color, float scale = 1f, int alpha = 255, Color? ink = null)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Below about 28 px the thin italic strokes vanish: use the bold italic there.
        using var p = Build("ı", bold: r.Height < 28);
        Fit(p, r, scale);
        using var inkBrush = new SolidBrush(Color.FromArgb(alpha, ink ?? Color.White));
        using var accent = new SolidBrush(Color.FromArgb(alpha, color));
        g.FillPath(inkBrush, p.Letters);
        g.FillPath(accent, p.Accent);
    }

    /// <summary>The app icon: the mark in ink and orange on a rounded paper tile.</summary>
    public static void DrawIcon(Graphics g, RectangleF r)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float radius = r.Width * 0.22f;
        using (var tile = RoundRect(r, radius))
        {
            using var paper = new SolidBrush(Paper);
            g.FillPath(paper, tile);
            if (r.Width >= 24)
            {
                using var edge = new Pen(PaperEdge, Math.Max(1f, r.Width / 64f));
                g.DrawPath(edge, tile);
            }
        }
        // Small icons get a bigger mark so the dot and cursor survive.
        float pad = r.Width * (r.Width <= 20 ? 0.08f : r.Width <= 40 ? 0.14f : 0.2f);
        DrawMark(g, RectangleF.Inflate(r, -pad, -pad), Accent, 1f, 255, Ink);
    }

    /// <summary>The wordmark "inkling_" inside <paramref name="r"/>.</summary>
    public static void DrawWordmark(Graphics g, RectangleF r, Color ink)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var p = Build("ınkling");
        Fit(p, r);
        using var inkBrush = new SolidBrush(ink);
        using var accent = new SolidBrush(Accent);
        g.FillPath(inkBrush, p.Letters);
        g.FillPath(accent, p.Accent);
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Writes a multi-size .ico (PNG-compressed entries, supported since Windows Vista).</summary>
    public static void WriteIco(string path, params int[] sizes)
    {
        var pngs = sizes.Select(s =>
        {
            using var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                float pad = s <= 16 ? 0f : s * 0.04f;
                DrawIcon(g, new RectangleF(pad, pad, s - 2 * pad - 0.5f, s - 2 * pad - 0.5f));
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return (size: s, data: ms.ToArray());
        }).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)pngs.Count);
        int offset = 6 + 16 * pngs.Count;
        foreach (var (size, data) in pngs)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(data.Length); w.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in pngs) w.Write(data);
    }

    // ---- SVG export (README header and logo), with the letters as outlines so no font is needed --------------

    /// <summary>Writes header.svg, header-dark.svg and logo.svg into <paramref name="folder"/>.</summary>
    public static void WriteSvgs(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var (name, ink) in new[] { ("header", "#1F1F1F"), ("header-dark", "#F2F2F2") })
        {
            using var p = Build("ınkling");
            Fit(p, new RectangleF(8, 8, 560, 144));
            var b = p.Bounds;
            int w = (int)Math.Ceiling(b.Right + 8), h = 160;
            File.WriteAllText(Path.Combine(folder, name + ".svg"),
                $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {w} {h}\" width=\"{w}\" height=\"{h}\" role=\"img\" aria-label=\"Inkling\">\n" +
                $"<path fill=\"{ink}\" d=\"{Svg(p.Letters)}\"/>\n<path fill=\"#FF7D00\" d=\"{Svg(p.Accent)}\"/>\n</svg>\n");
        }
        // The icon tile.
        using (var tile = RoundRect(new RectangleF(16, 16, 480, 480), 106))
        using (var p = Build("ı"))
        {
            Fit(p, new RectangleF(112, 112, 288, 288));
            File.WriteAllText(Path.Combine(folder, "logo.svg"),
                "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 512 512\" width=\"512\" height=\"512\" role=\"img\" aria-label=\"Inkling\">\n" +
                $"<path fill=\"#FBF8F4\" stroke=\"#E6DED3\" stroke-width=\"8\" d=\"{Svg(tile)}\"/>\n" +
                $"<path fill=\"#1F1F1F\" d=\"{Svg(p.Letters)}\"/>\n<path fill=\"#FF7D00\" d=\"{Svg(p.Accent)}\"/>\n</svg>\n");
        }
    }

    /// <summary>A GDI+ path as SVG path data (lines and cubic Béziers).</summary>
    private static string Svg(GraphicsPath path)
    {
        if (path.PointCount == 0) return "";
        var pts = path.PathPoints;
        var types = path.PathTypes;
        var sb = new StringBuilder();
        string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        for (int i = 0; i < pts.Length; i++)
        {
            int t = types[i] & 0x7;
            if (t == 0) sb.Append($"M{F(pts[i].X)},{F(pts[i].Y)}");
            else if (t == 1) sb.Append($"L{F(pts[i].X)},{F(pts[i].Y)}");
            else if (t == 3 && i + 2 < pts.Length)
            {
                sb.Append($"C{F(pts[i].X)},{F(pts[i].Y)} {F(pts[i + 1].X)},{F(pts[i + 1].Y)} {F(pts[i + 2].X)},{F(pts[i + 2].Y)}");
                i += 2;
                if ((types[i] & 0x80) != 0) sb.Append('Z');
                continue;
            }
            if ((types[i] & 0x80) != 0) sb.Append('Z');
        }
        return sb.ToString();
    }
}
