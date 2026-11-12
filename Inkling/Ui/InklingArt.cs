using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Inkling.Ui;

/// <summary>The Inkling mark: a white ink drop on a coloured disc. Used for the .ico and the live indicator.</summary>
internal static class InklingArt
{
    public static readonly Color Violet = Color.FromArgb(124, 92, 255);
    public static readonly Color Amber = Color.FromArgb(245, 166, 35);
    public static readonly Color Green = Color.FromArgb(46, 189, 110);
    public static readonly Color Red = Color.FromArgb(232, 69, 69);

    /// <summary>Draws the disc + drop inside <paramref name="r"/>. <paramref name="dropScale"/> 1 = normal size.</summary>
    public static void DrawMark(Graphics g, RectangleF r, Color disc, float dropScale = 1f, int alpha = 255)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(Color.FromArgb(alpha, disc))) g.FillEllipse(b, r);
        using var drop = DropPath(r, dropScale);
        using var w = new SolidBrush(Color.FromArgb(alpha, Color.White));
        g.FillPath(w, drop);
    }

    /// <summary>
    /// Ink drop: a circle with two tangent lines meeting at a tip above it.
    /// For a tangent point at 40° above horizontal the tip sits r / sin(40°) ≈ 1.556 r above the centre.
    /// </summary>
    private static GraphicsPath DropPath(RectangleF r, float scale)
    {
        float size = Math.Min(r.Width, r.Height);
        float rad = size * 0.2f * scale;
        float cx = r.X + r.Width / 2f;
        float cy = r.Y + r.Height * 0.585f + (1 - scale) * size * 0.05f;
        var tip = new PointF(cx, cy - rad * 1.556f);
        var path = new GraphicsPath();
        // GDI+ angles: 0° = right, clockwise. From upper-right (320°) round the bottom to upper-left (220°).
        path.AddArc(cx - rad, cy - rad, rad * 2, rad * 2, 320f, 260f);
        path.AddLine(path.GetLastPoint(), tip);
        path.CloseFigure();
        return path;
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
                float pad = s <= 16 ? 0f : s * 0.03f;
                DrawMark(g, new RectangleF(pad, pad, s - 2 * pad - 0.5f, s - 2 * pad - 0.5f), Violet);
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
}
