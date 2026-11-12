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
        public void ClearCliContext() { }
        public string DiagnosticsText() => "";
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
        int width = (int)Math.Ceiling(root.ActualWidth), height = (int)Math.Ceiling(root.ActualHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Fluent paints the window backdrop outside the visual tree, so draw its plain colour here.
            var dark = w.ThemeMode == ThemeMode.Dark || (w.ThemeMode == ThemeMode.System && Theme.SystemUsesDark());
            dc.DrawRectangle(new SolidColorBrush(dark ? System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20) : System.Windows.Media.Color.FromRgb(0xF3, 0xF3, 0xF3)), null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        }
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
