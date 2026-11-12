using Microsoft.Win32;
using WpfWindow = System.Windows.Window;
using ThemeMode = System.Windows.ThemeMode;

namespace Inkling.Ui.Wpf;

/// <summary>Windows 11 Fluent look for Inkling's WPF windows, following the Theme setting (as in Circularity).</summary>
internal static class Theme
{
    public static void ApplyTo(WpfWindow window, string theme)
    {
        var mode = theme.ToLowerInvariant() switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        if (window.ThemeMode != mode) window.ThemeMode = mode;
    }

    public static bool SystemUsesDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }
}
