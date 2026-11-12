using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using WpfVisibility = System.Windows.Visibility;

namespace Inkling.Ui.Wpf;

/// <summary>Offers a newer release: its notes, and "Update now" (download + run the installer).</summary>
internal sealed partial class UpdateWindow : Window
{
    private readonly Release _release;
    private readonly Action<string> _skip;
    private CancellationTokenSource? _cts;

    public UpdateWindow(Release release, string theme, Action<string> skip)
    {
        _release = release;
        _skip = skip;
        InitializeComponent();
        Theme.ApplyTo(this, theme);
        Logo.Source = SettingsWindow.AppIcon();
        Heading.Text = $"Inkling {release.Version.ToString(3)} is available";
        Subheading.Text = $"You have {Updates.Current.ToString(3)}.";
        Notes.Text = Plain(release.Notes);
        if (release.InstallerUrl == null)
        {
            InstallButton.Content = "Open release page";
            Status.Text = "This release has no installer attached.";
        }
    }

    /// <summary>Release notes are Markdown; show them as readable plain text.</summary>
    private static string Plain(string md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "No notes for this release.";
        var s = md.Replace("\r\n", "\n");
        s = Regex.Replace(s, @"^#{1,6}\s*", "", RegexOptions.Multiline);
        s = Regex.Replace(s, @"^\s*[-*]\s+", "•  ", RegexOptions.Multiline);
        s = Regex.Replace(s, @"\*\*(.+?)\*\*|__(.+?)__", "$1$2");
        s = Regex.Replace(s, @"`([^`]*)`", "$1");
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return s.Trim();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_release.InstallerUrl == null) { Page_Click(sender, e); Close(); return; }
        InstallButton.IsEnabled = false;
        Progress.Visibility = WpfVisibility.Visible;
        Status.Text = "Downloading…";
        _cts = new CancellationTokenSource();
        try
        {
            await Updates.InstallAsync(_release, new Progress<double>(p => Progress.Value = p), _cts.Token);
            Status.Text = "Installing. Inkling restarts by itself when it's done.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write("Update failed: " + ex.Message);
            Status.Text = "Couldn't download the update: " + ex.Message;
            InstallButton.IsEnabled = true;
            Progress.Visibility = WpfVisibility.Collapsed;
        }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _skip(_release.Version.ToString(3));
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();

    private void Page_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_release.PageUrl) { UseShellExecute = true }); } catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        base.OnClosed(e);
    }
}
