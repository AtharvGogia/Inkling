using System.Diagnostics;
using System.Windows;
using Inkling.Ai;

namespace Inkling.Ui.Wpf;

/// <summary>Shown on first start: how Inkling works, the API key (saved and checked), and the main choices.</summary>
internal sealed partial class WelcomeWindow : Window
{
    private readonly ISettingsHost _host;
    private readonly Action _openSettings;

    public WelcomeWindow(ISettingsHost host, Action openSettings)
    {
        _host = host;
        _openSettings = openSettings;
        InitializeComponent();
        var cfg = host.Config;
        Theme.ApplyTo(this, cfg.Theme);
        Logo.Source = SettingsWindow.AppIcon();

        Step1.Text = $"Press {HotkeyBox.Pretty(cfg.Hotkey)}, type what you want, press Enter.";
        Step2.Text = cfg.SelectionHotkey.Length > 0
            ? $"Or select text and press {HotkeyBox.Pretty(cfg.SelectionHotkey)}."
            : "";
        Step3.Text = cfg.RetryHotkey.Length > 0
            ? $"Not quite right? {HotkeyBox.Pretty(cfg.RetryHotkey)} tries again."
            : "";

        StartupCheck.IsChecked = host.StartupEnabled;
        RiskCheck.IsChecked = cfg.RiskGuard;
        RefreshKeyStatus();
    }

    private void RefreshKeyStatus() =>
        KeyStatus.Text = Secrets.NimKeySource() switch
        {
            "stored" => "Saved. You're ready.",
            "environment" => "Found NVIDIA_API_KEY in your environment. You're ready.",
            _ => "Inkling answers with NVIDIA's free API, so it needs a key. It takes a minute:",
        };

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password.Trim();
        if (key.Length == 0) { TestResult.Text = "Paste a key first."; return; }
        try { Secrets.SaveNimKey(key); }
        catch (Exception ex) { TestResult.Text = ex.Message; return; }
        KeyBox.Clear();
        RefreshKeyStatus();
        Test_Click(sender, e);
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        TestResult.Text = "Testing…";
        var key = KeyBox.Password.Trim();
        var (ok, message) = await ChatApiBackend.TestAsync(_host.Config, key.Length > 0 ? key : null);
        TestResult.Text = (ok ? "✓ " : "✗ ") + message;
    }

    private void Nvidia_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppInfo.NvidiaKeysUrl) { UseShellExecute = true }); } catch { }
    }

    private void Tutorial_Click(object sender, RoutedEventArgs e) => _host.ShowTutorial();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _openSettings();
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        // Whatever button closed it, the choices shown here are kept and the guide isn't shown again.
        var cfg = _host.Config.Clone();
        cfg.FirstRunDone = true;
        cfg.RiskGuard = RiskCheck.IsChecked == true;
        _host.Apply(cfg);
        _host.StartupEnabled = StartupCheck.IsChecked == true;
        base.OnClosed(e);
    }
}
