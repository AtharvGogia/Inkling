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
/// Windows desktop (a terminal or Notepad, Inkling's i_rdy badge and confirm bar, on-screen key labels),
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
            for (int i = 0; i < n; i++) Save(Draw(scene, scene.At(i / (double)Fps), i / (double)Fps), Path.Combine(frames, $"{i:0000}.png"));
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
            if (t < 5.1) return new(Typed(ans, t, 4.4, 40), "", "", true, Light.Thinking, null, null);
            return new(ans, "", "", Blink(t), t < 6.3 ? Light.Done : Light.Off, null, null);
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
            if (t < 4.0) return new(Typed(ans, t, 3.6, 40), "", "", true, Light.Thinking, null, null);
            return new(ans, "", "", Blink(t), t < 5.2 ? Light.Done : Light.Off, null, null);
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
            if (t < 3.65) return new(Typed("joyful", t, 3.4, 30), "", "", true, Light.Thinking, null, null);
            if (t < 4.6) return new("joyful", "", "", Blink(t), Light.Done, null, null);
            if (t < 5.8) return new(t < 4.75 ? "joyful" : Dots(t, 4.75), "", "", true, Light.Thinking, t < 5.3 ? "Ctrl + Alt + R" : null, null);
            if (t < 6.1) return new(Typed("cheerful", t, 5.8, 30), "", "", true, Light.Thinking, null, null);
            return new("cheerful", "", "", Blink(t), t < 7.3 ? Light.Done : Light.Off, null, null);
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
            if (t < 7.1) return new(Typed(ans, t, 6.4, 40), "", "", true, Light.Thinking, null, null);
            return new(ans, "", "", Blink(t), t < 8.3 ? Light.Done : Light.Off, null, null);
        });
    }

    // ---- Drawing --------------------------------------------------------------------------------

    private static WpfBrush B(uint argb) =>
        new SolidColorBrush(WpfColor.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    private static FrameworkElement Draw(Scene s, Frame f, double t)
    {
        var root = new Canvas { Width = W, Height = H };
        root.Background = new LinearGradientBrush(WpfColor.FromRgb(0x26, 0x46, 0x53), WpfColor.FromRgb(0xFF, 0x7D, 0x00), 35);
        // Soft shapes on the wallpaper.
        Add(root, new System.Windows.Shapes.Ellipse { Width = 520, Height = 520, Fill = B(0x22FFFFFF) }, -140, 220);
        Add(root, new System.Windows.Shapes.Ellipse { Width = 380, Height = 380, Fill = B(0x18FFFFFF) }, 700, -120);

        Add(root, Taskbar(s.Terminal), 0, H - 44);

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

        // The "i_rdy" badge, top-right of the screen, as Inkling draws it (dark theme).
        if (f.Light != Light.Off)
        {
            var mode = f.Light switch { Light.Thinking => BadgeIndicator.Mode.Thinking, Light.Done => BadgeIndicator.Mode.Done, _ => BadgeIndicator.Mode.Ready };
            var img = Badge(mode, BadgeIndicator.TextAt(mode, mode == BadgeIndicator.Mode.Done ? 0 : t) ?? "i_rdy");
            Add(root, new WpfImage { Source = img, Width = img.PixelWidth, Height = img.PixelHeight }, W - 8 - img.PixelWidth, 8);
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

    // ---- Taskbar: Windows 11 style, drawn with Windows' own icon font (Segoe Fluent Icons) --------------

    private static FrameworkElement Taskbar(bool terminalActive)
    {
        var bar = new Grid { Width = W, Height = 44, Background = B(0xF01C1C1C) };
        bar.Children.Add(new WpfBorder { Height = 1, VerticalAlignment = VerticalAlignment.Top, Background = B(0x30FFFFFF) });

        var apps = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        apps.Children.Add(TaskbarButton(StartLogo(), running: false, active: false));
        apps.Children.Add(TaskbarButton(Glyph("\uE721", 17, 0xFFE6E6E6), false, false));                   // Search
        apps.Children.Add(TaskbarButton(Glyph("\uE8D5", 20, 0xFFF4C44E), true, false));                    // File Explorer
        apps.Children.Add(TaskbarButton(TerminalIcon(), true, terminalActive));                                // Terminal
        apps.Children.Add(TaskbarButton(NotepadIcon(), true, !terminalActive));                                // Notepad
        apps.Children.Add(TaskbarButton(Glyph("\uE713", 18, 0xFFB8B8B8), false, false));                   // Settings
        bar.Children.Add(apps);

        // System tray: overflow, Inkling's own icon, Wi-Fi, volume, battery, clock.
        var tray = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        tray.Children.Add(TrayItem(Glyph("\uE70E", 11, 0xFFE6E6E6)));
        tray.Children.Add(TrayItem(new WpfImage { Source = TrayIcon(), Width = 16, Height = 16 }));
        var status = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var g in new[] { "\uE701", "\uE767", "\uE83F" })
            status.Children.Add(new WpfBorder { Margin = new Thickness(4, 0, 4, 0), Child = Glyph(g, 14, 0xFFE6E6E6) });
        tray.Children.Add(status);
        var clock = new StackPanel { Margin = new Thickness(6, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        clock.Children.Add(new WpfTextBlock { Text = "1:47 PM", FontFamily = Ui, FontSize = 11.5, Foreground = B(0xFFE6E6E6), HorizontalAlignment = System.Windows.HorizontalAlignment.Right });
        clock.Children.Add(new WpfTextBlock { Text = "13-11-2026", FontFamily = Ui, FontSize = 11.5, Foreground = B(0xFFE6E6E6), HorizontalAlignment = System.Windows.HorizontalAlignment.Right });
        tray.Children.Add(clock);
        bar.Children.Add(tray);
        return bar;
    }

    private static WpfTextBlock Glyph(string g, double size, uint color) => new()
    {
        Text = g, FontFamily = Icons, FontSize = size, Foreground = B(color),
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A 40 px taskbar slot; running apps get a short line underneath, the active one a longer accent line.</summary>
    private static FrameworkElement TaskbarButton(FrameworkElement icon, bool running, bool active)
    {
        var g = new Grid { Width = 40, Height = 40, Margin = new Thickness(2, 0, 2, 0) };
        if (active) g.Children.Add(new WpfBorder { CornerRadius = new CornerRadius(4), Background = B(0x18FFFFFF), BorderBrush = B(0x14FFFFFF), BorderThickness = new Thickness(1) });
        var holder = new WpfBorder { Width = 24, Height = 24, Child = icon, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 3) };
        g.Children.Add(holder);
        if (running)
            g.Children.Add(new WpfBorder
            {
                Width = active ? 16 : 6, Height = 3, CornerRadius = new CornerRadius(1.5), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 2),
                Background = B(active ? 0xFF60CDFF : 0xFF9A9A9A),
            });
        return g;
    }

    private static FrameworkElement TrayItem(UIElement content) =>
        new WpfBorder { Width = 26, Height = 32, Child = content, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>The Windows logo: four squares in Windows 11's blue.</summary>
    private static FrameworkElement StartLogo()
    {
        var g = new Grid { Width = 19, Height = 19 };
        foreach (var (x, y) in new[] { (0, 0), (10, 0), (0, 10), (10, 10) })
            g.Children.Add(new WpfBorder
            {
                Width = 9, Height = 9, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(x, y, 0, 0), CornerRadius = new CornerRadius(1),
                Background = new LinearGradientBrush(WpfColor.FromRgb(0x4C, 0xC2, 0xFF), WpfColor.FromRgb(0x0A, 0x78, 0xD4), 90),
            });
        return g;
    }

    private static FrameworkElement TerminalIcon() => new WpfBorder
    {
        Width = 22, Height = 20, CornerRadius = new CornerRadius(4), Background = B(0xFF2B2B2B), BorderBrush = B(0xFF5A5A5A), BorderThickness = new Thickness(1),
        Child = new WpfTextBlock { Text = ">_", FontFamily = Mono, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = B(0xFFE6E6E6), Margin = new Thickness(3, 2, 0, 0) },
    };

    private static FrameworkElement NotepadIcon()
    {
        var lines = new StackPanel { Margin = new Thickness(4, 5, 4, 0) };
        foreach (var w in new[] { 12.0, 10, 12, 7 })
            lines.Children.Add(new WpfBorder { Width = w, Height = 1.6, Background = B(0xFF1F5FA8), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 2.4) });
        return new WpfBorder { Width = 19, Height = 22, CornerRadius = new CornerRadius(3), Background = B(0xFFE8F1FB), BorderBrush = B(0xFF6FA8DC), BorderThickness = new Thickness(1), Child = lines };
    }

    private static BitmapSource? _trayIcon;

    /// <summary>Inkling's tray icon, drawn by the same code as the real one.</summary>
    private static BitmapSource TrayIcon()
    {
        if (_trayIcon != null) return _trayIcon;
        using var bmp = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) InklingArt.DrawIcon(g, new System.Drawing.RectangleF(1, 1, 30, 30));
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var img = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        img.Freeze();
        return _trayIcon = img;
    }

    private static readonly Dictionary<string, BitmapSource> Badges = [];

    /// <summary>The badge drawn by the app's own code, at the size it has on a 100% display.</summary>
    private static BitmapSource Badge(BadgeIndicator.Mode mode, string text)
    {
        if (Badges.TryGetValue(mode + text, out var cached)) return cached;
        System.Drawing.Size size;
        using (var font = BadgeIndicator.MonoFont(12f))
        {
            var m = TextRenderer.MeasureText(BadgeIndicator.Widest, font, System.Drawing.Size.Empty, TextFormatFlags.NoPadding);
            size = new System.Drawing.Size(m.Width + 14, m.Height + 6);
        }
        using var bmp = new System.Drawing.Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) BadgeIndicator.DrawBadge(g, size, 1f, text, BadgeIndicator.ColorFor(mode), dark: true);
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var img = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        img.Freeze();
        return Badges[mode + text] = img;
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
