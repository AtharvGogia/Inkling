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

        Step1.Text = $"1.  Press {HotkeyBox.Pretty(cfg.Hotkey)} anywhere you can type, write your request, press Enter:";
        Step2.Text = cfg.SelectionHotkey.Length > 0
            ? $"2.  Or select some text and press {HotkeyBox.Pretty(cfg.SelectionHotkey)}: the selection is the request, and the answer replaces it."
            : "2.  Select-to-ask is off (turn it on in Settings).";
        Step3.Text = cfg.RetryHotkey.Length > 0
            ? $"3.  Not what you wanted? Press {HotkeyBox.Pretty(cfg.RetryHotkey)} right away for a different answer."
            : "3.  Try-again is off (turn it on in Settings).";

        StartupCheck.IsChecked = host.StartupEnabled;
        RiskCheck.IsChecked = cfg.RiskGuard;
        RefreshKeyStatus();
    }

    private void RefreshKeyStatus() =>
        KeyStatus.Text = Secrets.NimKeySource() switch
        {
            "stored" => "A key is already saved. You're ready to go; test it below if you like.",
            "environment" => "Found a key in the NVIDIA_API_KEY environment variable, so you're ready to go. You can also save one here.",
            _ => "Inkling uses NVIDIA's API to answer. Paste your API key:",
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
        try { Process.Start(new ProcessStartInfo("https://build.nvidia.com/") { UseShellExecute = true }); } catch { }
    }

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
