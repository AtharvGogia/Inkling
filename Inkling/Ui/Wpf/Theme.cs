using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using WpfColor = System.Windows.Media.Color;
using WpfWindow = System.Windows.Window;

namespace Inkling.Ui.Wpf;

/// <summary>Windows 11 Fluent look for Inkling's WPF windows, following the Theme setting, in Inkling's own colour.</summary>
internal static class Theme
{
    /// <summary>Fluent's accent steps, mapped to #FF7D00 and lighter/darker steps of it.</summary>
    private static readonly (string Key, WpfColor Ours)[] Steps =
    [
        ("SystemAccentColor", WpfColor.FromRgb(0xFF, 0x7D, 0x00)),
        ("SystemAccentColorLight1", WpfColor.FromRgb(0xFF, 0x96, 0x33)),
        ("SystemAccentColorLight2", WpfColor.FromRgb(0xFF, 0xB0, 0x66)),
        ("SystemAccentColorLight3", WpfColor.FromRgb(0xFF, 0xCB, 0x99)),
        ("SystemAccentColorDark1", WpfColor.FromRgb(0xE0, 0x6C, 0x00)),
        ("SystemAccentColorDark2", WpfColor.FromRgb(0xB8, 0x58, 0x00)),
        ("SystemAccentColorDark3", WpfColor.FromRgb(0x8A, 0x42, 0x00)),
    ];

    public static void ApplyTo(WpfWindow window, string theme)
    {
        var mode = theme.ToLowerInvariant() switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        if (window.ThemeMode != mode) window.ThemeMode = mode;
        // WPF (re)loads the Fluent dictionaries for the new mode; recolour once they're in.
        window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Recolor(window));
    }

    /// <summary>
    /// Fluent bakes the Windows accent colour into dozens of brushes (checkboxes, radio buttons, sliders, default
    /// buttons, focus rings). Find every brush and colour that uses one of the accent steps and override it on the
    /// window with the matching Inkling step; keys on the window itself win over the merged theme dictionaries.
    /// </summary>
    private static void Recolor(WpfWindow window)
    {
        var all = Flatten(window.Resources.MergedDictionaries).ToList();
        // The theme's accent keys point at Windows' accent palette; the brushes hold the resolved colours.
        WpfColor[] system =
        [
            System.Windows.SystemColors.AccentColor, System.Windows.SystemColors.AccentColorLight1, System.Windows.SystemColors.AccentColorLight2, System.Windows.SystemColors.AccentColorLight3,
            System.Windows.SystemColors.AccentColorDark1, System.Windows.SystemColors.AccentColorDark2, System.Windows.SystemColors.AccentColorDark3,
        ];
        var map = new Dictionary<WpfColor, WpfColor>();
        for (int i = 0; i < Steps.Length; i++)
        {
            map.TryAdd(Opaque(system[i]), Steps[i].Ours);
            window.Resources[Steps[i].Key] = Steps[i].Ours;
        }

        foreach (var d in all)
            foreach (var key in d.Keys)
            {
                object? value;
                try { value = d[key]; } catch { continue; }
                switch (value)
                {
                    case SolidColorBrush b when map.TryGetValue(Opaque(b.Color), out var c):
                        var brush = new SolidColorBrush(WpfColor.FromArgb(b.Color.A, c.R, c.G, c.B)) { Opacity = b.Opacity };
                        brush.Freeze();
                        window.Resources[key] = brush;
                        break;
                    case WpfColor col when map.TryGetValue(Opaque(col), out var c2):
                        window.Resources[key] = WpfColor.FromArgb(col.A, c2.R, c2.G, c2.B);
                        break;
                }
            }
    }

    private static WpfColor Opaque(WpfColor c) => WpfColor.FromRgb(c.R, c.G, c.B);

    private static IEnumerable<ResourceDictionary> Flatten(IEnumerable<ResourceDictionary> dicts)
    {
        foreach (var d in dicts)
        {
            yield return d;
            foreach (var inner in Flatten(d.MergedDictionaries)) yield return inner;
        }
    }

    public static bool SystemUsesDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }
}
