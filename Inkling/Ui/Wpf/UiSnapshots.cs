using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Inkling.Ui.Wpf;

/// <summary>
/// Dev check: Inkling.exe --render-ui &lt;folder&gt; [light|dark] renders the Settings tabs and the Welcome window
/// to PNGs off-screen, with a stand-in host that changes nothing (no config saves, no registry writes).
/// </summary>
internal static class UiSnapshots
{
    private sealed class DryHost(AppConfig cfg) : ISettingsHost
    {
        public AppConfig Config { get; private set; } = cfg;
        public void Apply(AppConfig c) => Config = c;
        public void SetHotkeysSuspended(bool suspended) { }
        public bool StartupEnabled { get; set; } = true;
        public void ShowTimings() { }
        public void ShowWelcome() { }
        public void ShowTutorial() { }
        public void ClearCliContext() { }
        public string DiagnosticsText() => "";
        public Task<string> CheckForUpdatesNowAsync() => Task.FromResult("You're up to date.");
    }

    public static int Run(string folder, string? theme)
    {
        Directory.CreateDirectory(folder);
        _ = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var cfg = AppConfig.Load().Clone();
        if (theme != null) cfg.Theme = theme;
        var host = new DryHost(cfg);

        var settings = new SettingsWindow(host) { Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
        settings.Show();
        for (int i = 0; i < settings.Tabs.Items.Count; i++)
        {
            settings.Tabs.SelectedIndex = i;
            if (settings.Tabs.Items[i] is System.Windows.Controls.TabItem { Header: string h } && h == "Model")
                foreach (var ex in FindAll<System.Windows.Controls.Expander>(settings)) ex.IsExpanded = true;
            Save(settings, Path.Combine(folder, $"settings-{i + 1}-{Header(settings, i)}.png"));
        }
        settings.Close();

        var tutorial = new TutorialWindow(cfg) { Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
        tutorial.Show();
        foreach (var (chapter, name) in new[] { (1, "tutorial-key"), (11, "tutorial-cli") })
        {
            tutorial.Open(chapter);
            Save(tutorial, Path.Combine(folder, name + ".png"));
        }
        tutorial.Close();

        var welcome = new WelcomeWindow(host, () => { }) { Left = -20000, Top = 0, ShowActivated = false, ShowInTaskbar = false };
        welcome.Show();
        Save(welcome, Path.Combine(folder, "welcome.png"));
        welcome.Close();
        Console.WriteLine("Rendered to " + Path.GetFullPath(folder));
        return 0;
    }

    private static string Header(SettingsWindow w, int i) =>
        (w.Tabs.Items[i] as System.Windows.Controls.TabItem)?.Header?.ToString()?.Replace(" & ", "-").Replace(' ', '-').ToLowerInvariant() ?? i.ToString();

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var x in FindAll<T>(child)) yield return x;
        }
    }

    private static void Save(Window w, string path)
    {
        // Let layout and data binding settle.
        w.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        w.UpdateLayout();
        w.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var root = (FrameworkElement)w.Content;
        var m = root.Margin;
        // The window's client area: the content plus its margin, at exact size (no stretching to the drawn bounds).
        double cw = Math.Ceiling(root.ActualWidth + m.Left + m.Right), ch = Math.Ceiling(root.ActualHeight + m.Top + m.Bottom);
        const double titleH = 32, pad = 12, radius = 8;
        double width = cw + 2 * pad, height = ch + titleH + 2 * pad;

        var dark = w.ThemeMode == ThemeMode.Dark || (w.ThemeMode == ThemeMode.System && Theme.SystemUsesDark());
        System.Windows.Media.Color C(uint rgb) => System.Windows.Media.Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        var backdrop = new SolidColorBrush(dark ? C(0x202020) : C(0xF3F3F3));
        var titleBar = new SolidColorBrush(dark ? C(0x2B2B2B) : C(0xEBEBEB));
        var border = new System.Windows.Media.Pen(new SolidColorBrush(dark ? C(0x3C3C3C) : C(0xCFCFCF)), 1);
        var text = new SolidColorBrush(dark ? C(0xE6E6E6) : C(0x1F1F1F));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // A window frame around the client area, so the picture reads as a window in the README.
            var frame = new Rect(pad + 0.5, pad + 0.5, cw - 1, ch + titleH - 1);
            dc.DrawRoundedRectangle(backdrop, border, frame, radius, radius);
            dc.PushClip(new RectangleGeometry(frame, radius, radius));
            dc.DrawRectangle(titleBar, null, new Rect(pad, pad, cw, titleH));
            var dpi = VisualTreeHelper.GetDpi(visual).PixelsPerDip;
            var title = new FormattedText(w.Title, System.Globalization.CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, text, dpi);
            dc.DrawText(title, new System.Windows.Point(pad + 14, pad + (titleH - title.Height) / 2));
            var icons = new Typeface("Segoe Fluent Icons");
            string[] glyphs = ["\uE921", "\uE922", "\uE8BB"];
            for (int i = 0; i < 3; i++)
            {
                var g = new FormattedText(glyphs[i], System.Globalization.CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, icons, 10, text, dpi);
                dc.DrawText(g, new System.Windows.Point(pad + cw - 46 * (3 - i) + (46 - g.Width) / 2, pad + (titleH - g.Height) / 2));
            }
            // The content, unscaled: the brush's viewbox is the element's own layout box, not its drawn bounds.
            var brush = new VisualBrush(root)
            {
                Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                // The visual's own coordinates already include its margin: (0, 0) is the window's client corner.
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, cw, ch),
            };
            dc.DrawRectangle(brush, null, new Rect(pad, pad + titleH, cw, ch));
            dc.Pop();
        }
        var bmp = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
