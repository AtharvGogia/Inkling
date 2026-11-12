using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Inkling.Ai;
using Inkling.Input;
using WpfRadioButton = System.Windows.Controls.RadioButton;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfVisibility = System.Windows.Visibility;
using WpfClipboard = System.Windows.Clipboard;

namespace Inkling.Ui.Wpf;

/// <summary>What the settings window needs from the running app.</summary>
internal interface ISettingsHost
{
    AppConfig Config { get; }
    /// <summary>Save and apply a changed config straight away.</summary>
    void Apply(AppConfig cfg);
    /// <summary>Pause Inkling's own shortcuts while a shortcut box is recording.</summary>
    void SetHotkeysSuspended(bool suspended);
    bool StartupEnabled { get; set; }
    void ShowTimings();
    void ShowWelcome();
    void ClearCliContext();
    string DiagnosticsText();
    /// <summary>Checks GitHub now (opening the update window if there's one) and returns a short status line.</summary>
    Task<string> CheckForUpdatesNowAsync();
}

internal sealed partial class SettingsWindow : Window
{
    private readonly ISettingsHost _host;
    private AppConfig _cfg;
    private bool _loading;

    public SettingsWindow(ISettingsHost host)
    {
        _host = host;
        _cfg = host.Config.Clone();
        InitializeComponent();
        Theme.ApplyTo(this, _cfg.Theme);
        Wordmark.Source = WordmarkImage(_cfg.Theme);
        VersionText.Text = $"Inkling {AppInfo.Version}";

        foreach (var box in new[] { AskHotkey, SelectionHotkey, RetryHotkey })
            box.RecordingChanged += recording => _host.SetHotkeysSuspended(recording);
        AskHotkey.Changed += v => HotkeyChanged(AskHotkey, AskProblem, v, (c, x) => c.Hotkey = x, c => c.Hotkey);
        SelectionHotkey.Changed += v => HotkeyChanged(SelectionHotkey, SelectionProblem, v, (c, x) => c.SelectionHotkey = x, c => c.SelectionHotkey);
        RetryHotkey.Changed += v => HotkeyChanged(RetryHotkey, RetryProblem, v, (c, x) => c.RetryHotkey = x, c => c.RetryHotkey);
        // Backspace/Esc end a recording without raising Changed; turning one off must still be saved.
        SelectionHotkey.LostKeyboardFocus += (_, _) => { if (SelectionHotkey.Value.Length == 0 && _host.Config.SelectionHotkey.Length > 0) Set(c => c.SelectionHotkey = ""); };
        RetryHotkey.LostKeyboardFocus += (_, _) => { if (RetryHotkey.Value.Length == 0 && _host.Config.RetryHotkey.Length > 0) Set(c => c.RetryHotkey = ""); };

        foreach (var (rb, len) in new[] { (LengthShort, "short"), (LengthSentence, "sentence"), (LengthParagraph, "paragraph") })
            rb.Checked += (_, _) => Set(c => c.AnswerLength = len);
        foreach (var (rb, theme) in new[] { (ThemeSystem, "system"), (ThemeLight, "light"), (ThemeDark, "dark") })
            rb.Checked += (_, _) => { Set(c => c.Theme = theme); Theme.ApplyTo(this, theme); Wordmark.Source = WordmarkImage(theme); };
        StartupCheck.Click += (_, _) => _host.StartupEnabled = StartupCheck.IsChecked == true;

        foreach (var (rb, backend) in new[] { (BackendNim, "nim"), (BackendClaude, "claude"), (BackendAgy, "agy") })
            rb.Checked += (_, _) => Set(c => c.Backend = backend);

        IndicatorIcon.Checked += (_, _) => Set(c => c.Indicator = "icon");
        IndicatorBar.Checked += (_, _) => Set(c => c.Indicator = "bar");
        DotsCheck.Click += (_, _) => Set(c => c.ThinkingDots = DotsCheck.IsChecked == true);
        SpeedSlider.ValueChanged += (_, e) => { SpeedText.Text = $"{e.NewValue:0} ms"; Set(c => c.TypingDelayMs = (int)e.NewValue); };
        SlowSpeedSlider.ValueChanged += (_, e) => { SlowSpeedText.Text = $"{e.NewValue:0} ms"; Set(c => c.SlowAppDelayMs = (int)e.NewValue); };
        SlowApps.Changed += list => Set(c => c.SlowApps = list);
        SingleLineCheck.Click += (_, _) => Set(c => c.SingleLine = SingleLineCheck.IsChecked == true);

        RiskCheck.Click += (_, _) => Set(c => c.RiskGuard = RiskCheck.IsChecked == true);
        DisabledApps.Changed += list => Set(c => c.DisabledApps = list);
        TitleCheck.Click += (_, _) => Set(c => c.SendWindowTitle = TitleCheck.IsChecked == true);
        SnippetList.Changed += list => Set(c => c.Snippets = list);
        UpdatesCheck.Click += (_, _) => Set(c => c.CheckForUpdates = UpdatesCheck.IsChecked == true);

        LoadFromConfig();
    }

    // ---- Load / save --------------------------------------------------------------------------

    private void LoadFromConfig()
    {
        _loading = true;
        try
        {
            AskHotkey.Value = HotkeyBox.Pretty(_cfg.Hotkey);
            SelectionHotkey.Value = HotkeyBox.Pretty(_cfg.SelectionHotkey);
            RetryHotkey.Value = HotkeyBox.Pretty(_cfg.RetryHotkey);
            foreach (var p in new[] { AskProblem, SelectionProblem, RetryProblem }) p.Visibility = WpfVisibility.Collapsed;

            (Prompt.NormalizeLength(_cfg.AnswerLength) switch { "sentence" => LengthSentence, "paragraph" => LengthParagraph, _ => LengthShort }).IsChecked = true;
            (_cfg.Theme.ToLowerInvariant() switch { "light" => ThemeLight, "dark" => ThemeDark, _ => ThemeSystem }).IsChecked = true;
            StartupCheck.IsChecked = _host.StartupEnabled;

            BuildModelList();
            BackendClaude.Content = $"Claude Code CLI ({_cfg.ClaudeModel})";
            BackendAgy.Content = $"Gemini via the Antigravity CLI ({_cfg.AgyModel})";
            (_cfg.Backend.ToLowerInvariant() switch { "claude" => BackendClaude, "agy" or "gemini" => BackendAgy, _ => BackendNim }).IsChecked = true;
            RefreshKeyStatus();

            (_cfg.Indicator.Equals("bar", StringComparison.OrdinalIgnoreCase) ? IndicatorBar : IndicatorIcon).IsChecked = true;
            DotsCheck.IsChecked = _cfg.ThinkingDots;
            SpeedSlider.Value = Math.Clamp(_cfg.TypingDelayMs, 1, 25);
            SpeedText.Text = $"{SpeedSlider.Value:0} ms";
            SlowSpeedSlider.Value = Math.Clamp(_cfg.SlowAppDelayMs, 10, 80);
            SlowSpeedText.Text = $"{SlowSpeedSlider.Value:0} ms";
            SlowApps.Items = _cfg.SlowApps;
            SingleLineCheck.IsChecked = _cfg.SingleLine;

            RiskCheck.IsChecked = _cfg.RiskGuard;
            DisabledApps.Items = _cfg.DisabledApps;
            TitleCheck.IsChecked = _cfg.SendWindowTitle;
            SnippetList.Items = _cfg.Snippets;
            UpdatesCheck.IsChecked = _cfg.CheckForUpdates;
            UpdateStatus.Text = $"You have {AppInfo.Version}.";
        }
        finally { _loading = false; }
        RefreshStatus();
    }

    /// <summary>
    /// Applies one change on top of the app's current config, not this window's copy: the tray menu, the update
    /// check or a skipped version may have changed something since the window opened, and must not be undone.
    /// </summary>
    private void Set(Action<AppConfig> change)
    {
        if (_loading) return;
        _cfg = _host.Config.Clone();
        change(_cfg);
        Commit();
    }

    /// <summary>Something outside this window changed the config (tray menu): show it.</summary>
    public void Reload()
    {
        _cfg = _host.Config.Clone();
        LoadFromConfig();
    }

    private void Commit()
    {
        _host.Apply(_cfg.Clone());
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        bool api = _cfg.Backend.ToLowerInvariant() is not ("claude" or "agy" or "gemini");
        StatusModel.Text = _cfg.Backend.ToLowerInvariant() switch
        {
            "claude" => $"Claude Code CLI",
            "agy" or "gemini" => $"Gemini CLI",
            _ => AppConfig.ModelName(_cfg.NimModel),
        };
        StatusKey.Text = !api ? "Command-line tool" : Secrets.NimKeySource() == null ? "No API key yet" : "Ready";
        StatusHotkeys.Text = HotkeyBox.Pretty(_cfg.Hotkey) + " to ask";
    }

    // ---- Shortcuts -----------------------------------------------------------------------------

    private void HotkeyChanged(HotkeyBox box, WpfTextBlock problem, string value, Action<AppConfig, string> set, Func<AppConfig, string> get)
    {
        _cfg = _host.Config.Clone();
        string? blocking = null, warning = null;
        try
        {
            var hk = Hotkey.Parse(value);
            var others = new[] { (AskHotkey, _cfg.Hotkey), (SelectionHotkey, _cfg.SelectionHotkey), (RetryHotkey, _cfg.RetryHotkey) }
                .Where(o => o.Item1 != box && o.Item2.Length > 0);
            if (others.Any(o => Hotkey.Parse(o.Item2) == hk)) blocking = $"{HotkeyBox.Pretty(value)} is already one of Inkling's other shortcuts.";
            else if (HotkeyBox.Problem(value) is { } p)
            {
                // Shortcuts Windows or every app relies on would stop working everywhere: refuse those.
                if (p.StartsWith("Another program", StringComparison.Ordinal)) warning = p + " Inkling will take it while it runs.";
                else blocking = p;
            }
        }
        catch (FormatException ex) { blocking = ex.Message; }

        if (blocking != null)
        {
            box.Value = HotkeyBox.Pretty(get(_cfg)); // keep the old one
            problem.Text = blocking + " Kept " + (get(_cfg).Length > 0 ? HotkeyBox.Pretty(get(_cfg)) : "it off") + ".";
            problem.Visibility = WpfVisibility.Visible;
            return;
        }
        problem.Text = warning ?? "";
        problem.Visibility = warning == null ? WpfVisibility.Collapsed : WpfVisibility.Visible;
        set(_cfg, value.Replace("Enter", "Return"));
        Commit();
    }

    // ---- Model and key -------------------------------------------------------------------------

    private void BuildModelList()
    {
        ModelList.Children.Clear();
        var choices = AppConfig.Models.ToList();
        if (!choices.Any(m => m.Id == _cfg.NimModel))
            choices.Add(new ModelChoice(_cfg.NimModel, _cfg.NimModel, "Set by hand in config.json."));
        foreach (var m in choices)
        {
            var rb = new WpfRadioButton { GroupName = "Model", Margin = new Thickness(0, 0, 0, 8), IsChecked = m.Id == _cfg.NimModel };
            var label = new StackPanel();
            label.Children.Add(new WpfTextBlock { Text = m.Name });
            label.Children.Add(new WpfTextBlock { Text = m.Description, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
            rb.Content = label;
            var id = m.Id;
            rb.Checked += (_, _) => Set(c => { c.NimModel = id; c.Backend = "nim"; BackendNim.IsChecked = true; });
            ModelList.Children.Add(rb);
        }
    }

    private void RefreshKeyStatus()
    {
        KeyStatus.Text = Secrets.NimKeySource() switch
        {
            "stored" => "Saved.",
            "environment" => "Using NVIDIA_API_KEY from your environment.",
            _ => "Not set.",
        };
        RemoveKeyButton.IsEnabled = Secrets.NimKeySource() == "stored";
    }

    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Password.Trim();
        if (key.Length == 0) { TestResult.Text = "Paste a key first."; return; }
        try { Secrets.SaveNimKey(key); }
        catch (Exception ex) { TestResult.Text = ex.Message; return; }
        KeyBox.Clear();
        RefreshKeyStatus();
        RefreshStatus();
        TestKey_Click(sender, e);
    }

    private async void TestKey_Click(object sender, RoutedEventArgs e)
    {
        TestResult.Text = "Testing…";
        var key = KeyBox.Password.Trim();
        var (ok, message) = await ChatApiBackend.TestAsync(_cfg, key.Length > 0 ? key : null);
        TestResult.Text = (ok ? "✓ " : "✗ ") + message;
    }

    private void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        Secrets.DeleteNimKey();
        RefreshKeyStatus();
        RefreshStatus();
        TestResult.Text = "Removed.";
    }

    private void ClearContext_Click(object sender, RoutedEventArgs e) => _host.ClearCliContext();

    // ---- About ---------------------------------------------------------------------------------

    private void Welcome_Click(object sender, RoutedEventArgs e) => _host.ShowWelcome();
    private void Timings_Click(object sender, RoutedEventArgs e) => _host.ShowTimings();
    private void OpenLog_Click(object sender, RoutedEventArgs e) => Open(Log.FilePath);
    private void OpenConfig_Click(object sender, RoutedEventArgs e) => Open(AppConfig.FilePath);
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => Open(AppConfig.Dir);
    private void Source_Click(object sender, RoutedEventArgs e) => Open(AppInfo.SourceUrl);
    private void NvidiaKey_Click(object sender, RoutedEventArgs e) => Open("https://build.nvidia.com/");
    private void Issues_Click(object sender, RoutedEventArgs e) => Open(AppInfo.SourceUrl + "/issues");

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        UpdateStatus.Text = "Checking…";
        UpdateStatus.Text = await _host.CheckForUpdatesNowAsync();
        CheckNowButton.IsEnabled = true;
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            WpfClipboard.SetText(_host.DiagnosticsText());
            CopiedText.Visibility = WpfVisibility.Visible;
        }
        catch { /* clipboard busy */ }
    }

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"Could not open {target}: {ex.Message}"); }
    }

    // ---- Buttons -------------------------------------------------------------------------------

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _cfg = _host.Config.Clone();
        var d = new AppConfig();
        // Only what this window shows; app lists, the prompt, examples and paths stay as they are.
        _cfg.Hotkey = d.Hotkey; _cfg.SelectionHotkey = d.SelectionHotkey; _cfg.RetryHotkey = d.RetryHotkey;
        _cfg.AnswerLength = d.AnswerLength; _cfg.Theme = d.Theme;
        _cfg.Backend = d.Backend; _cfg.NimModel = d.NimModel;
        _cfg.Indicator = d.Indicator; _cfg.ThinkingDots = d.ThinkingDots;
        _cfg.TypingDelayMs = d.TypingDelayMs; _cfg.SlowAppDelayMs = d.SlowAppDelayMs; _cfg.SingleLine = d.SingleLine;
        _cfg.RiskGuard = d.RiskGuard; _cfg.SendWindowTitle = d.SendWindowTitle; _cfg.CheckForUpdates = d.CheckForUpdates;
        Commit();
        LoadFromConfig();
        Theme.ApplyTo(this, _cfg.Theme);
        Wordmark.Source = WordmarkImage(_cfg.Theme);
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _host.SetHotkeysSuspended(false);
        base.OnClosed(e);
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(nint h);

    /// <summary>The app icon (the i mark on its paper tile), for window headers.</summary>
    internal static BitmapSource? AppIcon() => Render(128, 128, (g, w, h) => InklingArt.DrawIcon(g, new System.Drawing.RectangleF(2, 2, w - 4, h - 4)));

    /// <summary>The "inkling_" wordmark, light or dark to suit the theme.</summary>
    internal static BitmapSource? WordmarkImage(string theme)
    {
        bool dark = theme.Equals("dark", StringComparison.OrdinalIgnoreCase) || (!theme.Equals("light", StringComparison.OrdinalIgnoreCase) && Theme.SystemUsesDark());
        var ink = dark ? System.Drawing.Color.FromArgb(0xF2, 0xF2, 0xF2) : InklingArt.Ink;
        return Render(560, 140, (g, w, h) => InklingArt.DrawWordmark(g, new System.Drawing.RectangleF(2, 2, w - 4, h - 4), ink));
    }

    private static BitmapSource? Render(int w, int h, Action<System.Drawing.Graphics, int, int> draw)
    {
        try
        {
            using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) draw(g, w, h);
            var hb = bmp.GetHbitmap(System.Drawing.Color.FromArgb(0, 0, 0, 0));
            try { return Imaging.CreateBitmapSourceFromHBitmap(hb, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); }
            finally { DeleteObject(hb); }
        }
        catch { return null; }
    }
}
