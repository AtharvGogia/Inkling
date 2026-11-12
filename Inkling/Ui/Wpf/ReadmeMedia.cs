using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfBorder = System.Windows.Controls.Border;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfImage = System.Windows.Controls.Image;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace Inkling.Ui.Wpf;

/// <summary>
/// Dev tool: Inkling.exe --render-media &lt;folder&gt; renders the README GIFs. Each is a short scene on a mock
/// Windows desktop (a terminal or Notepad, Inkling's corner light and confirm bar, on-screen key labels),
/// drawn frame by frame and encoded with FFmpeg. Nothing on the real screen is recorded, nothing is sent.
/// </summary>
internal static class ReadmeMedia
{
    private const int W = 960, H = 540, Fps = 25;
    private static readonly WpfFontFamily Ui = new("Segoe UI Variable Text, Segoe UI");
    private static readonly WpfFontFamily Mono = new("Cascadia Mono, Consolas");
    private static readonly WpfFontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private enum Light { Off, Listening, Thinking, Done }

    /// <summary>What a frame shows: the line being edited, and the overlays.</summary>
    private sealed record Frame(string Before, string Selected, string After, bool Caret, Light Light, string? Key, string? Confirm);

    private sealed record Scene(string Name, bool Terminal, string WindowTitle, string Prompt, double Length, Func<double, Frame> At);

    public static int Render(string folder)
    {
        Directory.CreateDirectory(folder);
        _ = System.Windows.Application.Current ?? new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int exit = 0;
        foreach (var scene in new[] { AskScene(), SelectScene(), RetryScene(), RiskyScene() })
        {
            var frames = Path.Combine(Path.GetTempPath(), "inkling-frames-" + scene.Name);
            if (Directory.Exists(frames)) Directory.Delete(frames, true);
            Directory.CreateDirectory(frames);
            int n = (int)Math.Ceiling(scene.Length * Fps);
            for (int i = 0; i < n; i++) Save(Draw(scene, scene.At(i / (double)Fps)), Path.Combine(frames, $"{i:0000}.png"));
            var gif = Path.Combine(folder, scene.Name + ".gif");
            if (!Encode(frames, gif)) exit = 1;
            else Console.WriteLine($"{gif}: {new FileInfo(gif).Length / 1024} KB, {n} frames");
            Directory.Delete(frames, true);
        }
        return exit;
    }

    // ---- Scenes: time in seconds -> what's on screen ----------------------------------------------

    private static string Typed(string text, double t, double start, double cps) =>
        t < start ? "" : text[..Math.Min(text.Length, (int)((t - start) * cps))];

    private static string Dots(double t, double start) => new('.', 1 + (int)((t - start) / 0.13) % 3);

    private static bool Blink(double t) => (int)(t * 2) % 2 == 0;

    /// <summary>Hotkey, type a request, Enter, dots, the answer replaces it.</summary>
    private static Scene AskScene()
    {
        const string req = "find files over 100mb in home", ans = "find ~ -type f -size +100M";
        return new Scene("ask", true, "atharv@PC: ~/projects", "atharv@PC:~/projects$ ", 7.6, t =>
        {
            if (t < 0.9) return new("", "", "", Blink(t), Light.Off, null, null);
            if (t < 3.1) return new(Typed(req, t, 1.3, 16), "", "", true, Light.Listening, t < 1.9 ? "Ctrl + Alt + Space" : null, null);
            if (t < 4.4) return new(t < 3.25 ? req : Dots(t, 3.25), "", "", true, Light.Thinking, t < 3.9 ? "Enter" : null, null);
            if (t < 5.4) return new(Typed(ans, t, 4.4, 40), "", "", true, Light.Done, null, null);
            return new(ans, "", "", Blink(t), t < 6.0 ? Light.Done : Light.Off, null, null);
        });
    }

    /// <summary>Select a sentence, press the selection shortcut, the answer replaces the selection.</summary>
    private static Scene SelectScene()
    {
        const string text = "translate to french: good morning, my friend", ans = "Bonjour, mon ami";
        return new Scene("select", false, "notes.txt - Notepad", "", 6.8, t =>
        {
            if (t < 0.6) return new(text, "", "", Blink(t), Light.Off, null, null);
            if (t < 1.8)
            {
                int sel = Math.Min(text.Length, (int)((t - 0.6) / 1.0 * text.Length));
                return new(text[..^sel], text[^sel..], "", false, Light.Off, null, null);
            }
            if (t < 2.2) return new("", text, "", false, Light.Off, "Ctrl + Alt + Enter", null);
            if (t < 3.6) return new(Dots(t, 2.2), "", "", true, Light.Thinking, t < 2.9 ? "Ctrl + Alt + Enter" : null, null);
            if (t < 4.2) return new(Typed(ans, t, 3.6, 40), "", "", true, Light.Done, null, null);
            return new(ans, "", "", Blink(t), t < 4.8 ? Light.Done : Light.Off, null, null);
        });
    }

    /// <summary>An answer, then the retry shortcut swaps it for another.</summary>
    private static Scene RetryScene()
    {
        const string req = "another word for happy";
        return new Scene("retry", false, "essay.txt - Notepad", "It was a ", 8.2, t =>
        {
            if (t < 0.6) return new("", "", "", Blink(t), Light.Off, null, null);
            if (t < 2.4) return new(Typed(req, t, 1.0, 16), "", "", true, Light.Listening, t < 1.5 ? "Ctrl + Alt + Space" : null, null);
            if (t < 3.4) return new(t < 2.5 ? req : Dots(t, 2.5), "", "", true, Light.Thinking, t < 3.0 ? "Enter" : null, null);
            if (t < 4.6) return new(Typed("joyful", t, 3.4, 30), "", "", Blink(t), t < 4.0 ? Light.Done : Light.Off, null, null);
            if (t < 5.8) return new(t < 4.75 ? "joyful" : Dots(t, 4.75), "", "", true, Light.Thinking, t < 5.3 ? "Ctrl + Alt + R" : null, null);
            if (t < 6.4) return new(Typed("cheerful", t, 5.8, 30), "", "", true, Light.Done, null, null);
            return new("cheerful", "", "", Blink(t), t < 6.9 ? Light.Done : Light.Off, null, null);
        });
    }

    /// <summary>A destructive answer is shown first and only typed after Enter.</summary>
    private static Scene RiskyScene()
    {
        const string req = "delete all .log files in here", ans = "find . -name \"*.log\" -delete";
        const string bar = "Careful, this deletes every file it finds:  " + ans + "   ·   Enter = type it   ·   Esc = cancel";
        return new Scene("risky", true, "atharv@PC: ~/server/logs", "atharv@PC:~/server/logs$ ", 9.0, t =>
        {
            if (t < 0.6) return new("", "", "", Blink(t), Light.Off, null, null);
            if (t < 2.8) return new(Typed(req, t, 1.0, 16), "", "", true, Light.Listening, t < 1.5 ? "Ctrl + Alt + Space" : null, null);
            if (t < 3.8) return new(t < 2.9 ? req : Dots(t, 2.9), "", "", true, Light.Thinking, t < 3.4 ? "Enter" : null, null);
            if (t < 6.4) return new("...", "", "", false, Light.Off, t > 5.8 ? "Enter" : null, bar);
            if (t < 7.2) return new(Typed(ans, t, 6.4, 40), "", "", true, Light.Off, null, null);
            return new(ans, "", "", Blink(t), Light.Off, null, null);
        });
    }

    // ---- Drawing --------------------------------------------------------------------------------

    private static WpfBrush B(uint argb) =>
        new SolidColorBrush(WpfColor.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    private static FrameworkElement Draw(Scene s, Frame f)
    {
        var root = new Canvas { Width = W, Height = H };
        root.Background = new LinearGradientBrush(WpfColor.FromRgb(0x3B, 0x3F, 0x8C), WpfColor.FromRgb(0x9A, 0x7B, 0xD8), 35);
        // Soft shapes on the wallpaper.
        Add(root, new System.Windows.Shapes.Ellipse { Width = 520, Height = 520, Fill = B(0x22FFFFFF) }, -140, 220);
        Add(root, new System.Windows.Shapes.Ellipse { Width = 380, Height = 380, Fill = B(0x18FFFFFF) }, 700, -120);

        // Taskbar
        var taskbar = new WpfBorder { Width = W, Height = 44, Background = B(0xE6202024) };
        var icons = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in new uint[] { 0xFF3A8EE6, 0xFF2D2D30, 0xFFF2C94C, 0xFF6C5CE7 })
            icons.Children.Add(new WpfBorder { Width = 24, Height = 24, CornerRadius = new CornerRadius(5), Background = B(c), Margin = new Thickness(7, 0, 7, 0) });
        taskbar.Child = icons;
        Add(root, taskbar, 0, H - 44);

        // App window
        double wx = 110, wy = 70, ww = 740, wh = 340;
        bool dark = s.Terminal;
        var win = new WpfBorder
        {
            Width = ww, Height = wh, CornerRadius = new CornerRadius(8), ClipToBounds = true,
            Background = B(dark ? 0xFF0C0C0C : 0xFFFFFFFF), BorderBrush = B(0x40000000), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.35 },
        };
        var dock = new DockPanel();
        var title = new DockPanel { Height = 34, Background = B(dark ? 0xFF1F1F1F : 0xFFF3F3F3) };
        var caption = new StackPanel { Orientation = WpfOrientation.Horizontal };
        DockPanel.SetDock(caption, Dock.Right);
        foreach (var g in new[] { "", "", "" })
            caption.Children.Add(new WpfTextBlock { Text = g, FontFamily = Icons, FontSize = 10, Width = 46, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = B(dark ? 0xFFCCCCCC : 0xFF333333) });
        title.Children.Add(caption);
        title.Children.Add(new WpfTextBlock
        {
            Text = s.WindowTitle, FontFamily = Ui, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0),
            Foreground = B(dark ? 0xFFDDDDDD : 0xFF222222),
        });
        DockPanel.SetDock(title, Dock.Top);
        dock.Children.Add(title);

        var line = new WpfTextBlock
        {
            FontFamily = dark ? Mono : Ui, FontSize = dark ? 15 : 16, Margin = new Thickness(14, 12, 14, 0),
            Foreground = B(dark ? 0xFFE6E6E6 : 0xFF1A1A1A), TextWrapping = TextWrapping.Wrap,
        };
        if (s.Prompt.Length > 0)
        {
            if (dark)
            {
                var at = s.Prompt.IndexOf(':');
                line.Inlines.Add(new Run(s.Prompt[..at]) { Foreground = B(0xFF16C60C) });
                line.Inlines.Add(new Run(":"));
                line.Inlines.Add(new Run(s.Prompt[(at + 1)..^2]) { Foreground = B(0xFF3B78FF) });
                line.Inlines.Add(new Run("$ "));
            }
            else line.Inlines.Add(new Run(s.Prompt));
        }
        line.Inlines.Add(new Run(f.Before));
        if (f.Selected.Length > 0) line.Inlines.Add(new Run(f.Selected) { Background = B(0xFF0078D4), Foreground = B(0xFFFFFFFF) });
        if (f.Caret)
            line.Inlines.Add(new InlineUIContainer(new WpfBorder
            {
                Width = dark ? 9 : 1.5, Height = dark ? 18 : 20, Background = B(dark ? 0xFFE6E6E6 : 0xFF1A1A1A),
            }) { BaselineAlignment = BaselineAlignment.Center });
        line.Inlines.Add(new Run(f.After));
        dock.Children.Add(line);
        win.Child = dock;
        Add(root, win, wx, wy);

        // Risky-command bar, just under the line (as Inkling places it under the caret).
        if (f.Confirm != null)
        {
            var bar = new WpfBorder { Background = B(0xF01C1C20), Padding = new Thickness(12, 6, 12, 6), BorderBrush = B(0xFFE84545), BorderThickness = new Thickness(4, 0, 0, 0) };
            bar.Child = new WpfTextBlock { Text = "Inkling ▸ " + f.Confirm, FontFamily = Ui, FontSize = 12, Foreground = B(0xFFFFFFFF) };
            Add(root, bar, wx + 24, wy + 34 + 12 + 26);
        }

        // Corner light: top-left of the screen.
        if (f.Light != Light.Off)
        {
            var color = f.Light switch { Light.Thinking => InklingArt.Amber, Light.Done => InklingArt.Green, _ => InklingArt.Violet };
            var pill = new WpfBorder { Background = B(0xE61C1C20), CornerRadius = new CornerRadius(14), Padding = new Thickness(4) };
            var row = new StackPanel { Orientation = WpfOrientation.Horizontal };
            row.Children.Add(new WpfImage { Source = Mark(color), Width = 20, Height = 20 });
            if (f.Light == Light.Thinking)
                row.Children.Add(new WpfTextBlock { Text = " ...", Foreground = B(0xFFFFFFFF), FontFamily = Ui, FontSize = 13, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 2) });
            pill.Child = row;
            Add(root, pill, 8, 8);
        }

        // Key label, bottom centre.
        if (f.Key != null)
        {
            var keys = new StackPanel { Orientation = WpfOrientation.Horizontal };
            var parts = f.Key.Split(" + ");
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) keys.Children.Add(new WpfTextBlock { Text = "+", Foreground = B(0xCCFFFFFF), FontFamily = Ui, FontSize = 16, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                keys.Children.Add(new WpfBorder
                {
                    Background = B(0xFFF7F7F7), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 5, 12, 5),
                    BorderBrush = B(0xFFBDBDBD), BorderThickness = new Thickness(1, 1, 1, 3),
                    Child = new WpfTextBlock { Text = parts[i], FontFamily = Ui, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = B(0xFF222222) },
                });
            }
            var holder = new WpfBorder { Background = B(0x99000000), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Child = keys };
            holder.Measure(new WpfSize(W, H));
            Add(root, holder, (W - holder.DesiredSize.Width) / 2, H - 44 - 24 - holder.DesiredSize.Height);
        }
        return root;
    }

    private static readonly Dictionary<int, BitmapSource> Marks = [];

    private static BitmapSource Mark(System.Drawing.Color c)
    {
        if (Marks.TryGetValue(c.ToArgb(), out var cached)) return cached;
        using var bmp = new System.Drawing.Bitmap(64, 64, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) InklingArt.DrawMark(g, new System.Drawing.RectangleF(1, 1, 62, 62), c);
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var img = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        img.Freeze();
        return Marks[c.ToArgb()] = img;
    }

    private static void Add(Canvas c, UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        c.Children.Add(e);
    }

    private static void Save(FrameworkElement root, string path)
    {
        root.Measure(new WpfSize(W, H));
        root.Arrange(new Rect(0, 0, W, H));
        root.UpdateLayout();
        var bmp = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static bool Encode(string frames, string gif)
    {
        var info = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[]
        {
            "-hide_banner", "-loglevel", "error", "-y", "-framerate", Fps.ToString(CultureInfo.InvariantCulture), "-i", Path.Combine(frames, "%04d.png"),
            "-vf", "split[a][b];[a]palettegen=max_colors=192:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle",
            "-loop", "0", gif,
        })
            info.ArgumentList.Add(arg);
        try
        {
            using var p = Process.Start(info)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) Console.WriteLine(err);
            return p.ExitCode == 0;
        }
        catch (Exception ex) { Console.WriteLine("FFmpeg not found on PATH: " + ex.Message); return false; }
    }
}
