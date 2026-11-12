using System.Diagnostics;
using System.Text;
using Inkling.Ai;
using Inkling.Ui;
using Inkling.Ui.Wpf;
using Microsoft.Win32;
using ElementHost = System.Windows.Forms.Integration.ElementHost;

namespace Inkling;

/// <summary>Tray icon + menu; owns config, the active backend, the capture controller and the Settings/Welcome windows.</summary>
internal sealed class TrayApp : ApplicationContext, ISettingsHost
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Inkling";
    private const string OldRunValue = "InlineAI";

    private readonly NotifyIcon _tray;
    private readonly StatusOverlay _overlay;
    private readonly Control _ui;
    private readonly CaptureController _capture;
    private AppConfig _config;
    private IAiBackend _backend;
    private readonly ToolStripMenuItem _modelMenu, _pauseItem;
    private readonly List<(string Length, ToolStripMenuItem Item)> _lengthItems = [];
    private DiagnosticsForm? _diagnostics;
    private SettingsWindow? _settings;
    private WelcomeWindow? _welcome;
    private UpdateWindow? _updateWindow;
    private Release? _available;
    private readonly ToolStripMenuItem _updateItem;
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 60_000 };

    public TrayApp()
    {
        _config = AppConfig.Load();
        MigrateRunKey();
        // WPF windows (Settings, Welcome) run on this same UI thread; an Application object gives them
        // Application.Current and resources, while WinForms keeps running the message loop.
        if (System.Windows.Application.Current == null)
            _ = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };

        _backend = CreateBackend(_config);
        _ui = new Control();
        _ui.CreateControl(); // a UI-thread handle for BeginInvoke from the hooks
        _ = _ui.Handle;
        _overlay = new StatusOverlay { Style = StatusOverlay.Parse(_config.Indicator) };

        _capture = new CaptureController(_ui, () => _config, () => _backend, _overlay);
        _backend.Warm();

        _modelMenu = new ToolStripMenuItem("Model");
        _pauseItem = new ToolStripMenuItem("Pause Inkling", null, (_, _) => TogglePause());
        foreach (var length in Prompt.Lengths)
            _lengthItems.Add((length, new ToolStripMenuItem(Prompt.LengthLabel(length), null, (_, _) => SetLength(length))));

        _updateItem = new ToolStripMenuItem("", null, (_, _) => ShowUpdate()) { Visible = false };
        var menu = new ContextMenuStrip();
        menu.Items.Add(_updateItem);
        var settingsItem = new ToolStripMenuItem("Settings…", null, (_, _) => ShowSettings());
        settingsItem.Font = new Font(settingsItem.Font, FontStyle.Bold);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_modelMenu);
        menu.Items.Add(new ToolStripMenuItem("Answer length", null, [.. _lengthItems.Select(x => (ToolStripItem)x.Item)]));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Help", null,
            new ToolStripMenuItem("Welcome guide", null, (_, _) => ShowWelcome()),
            new ToolStripMenuItem("Check for updates…", null, async (_, _) =>
            {
                var status = await CheckForUpdatesNowAsync();
                if (_available == null) _tray!.ShowBalloonTip(2000, "Inkling", status, ToolTipIcon.Info);
            }),
            new ToolStripMenuItem("Request timings", null, (_, _) => ShowTimings()),
            new ToolStripMenuItem("Open log", null, (_, _) => OpenPath(Log.FilePath))));
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => RefreshMenu();

        _tray = new NotifyIcon { Icon = LoadIcon(), ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };
        _tray.BalloonTipClicked += (_, _) => { if (_available != null) ShowUpdate(); };
        // First automatic check a minute after start, then hourly ticks that check at most once a day.
        _updateTimer.Tick += async (_, _) => { _updateTimer.Interval = 3_600_000; await AutoCheckAsync(); };
        _updateTimer.Start();
        _overlay.Message += (text, isError) =>
            _tray.ShowBalloonTip(isError ? 4000 : 2000, "Inkling", text, isError ? ToolTipIcon.Warning : ToolTipIcon.Info);
        RefreshMenu();

        if (!_config.FirstRunDone) _ui.BeginInvoke(ShowWelcome);
        else
            _tray.ShowBalloonTip(2500, "Inkling is running",
                $"Press {HotkeyBox.Pretty(_capture.Hotkey.ToString())} anywhere, type a request, press Enter. Using {Describe(_config)}.", ToolTipIcon.Info);
    }

    public static IAiBackend CreateBackend(AppConfig cfg) => cfg.Backend.ToLowerInvariant() switch
    {
        "agy" or "gemini" => new AgyBackend(cfg),
        "claude" => new ClaudeBackend(cfg),
        _ => new ChatApiBackend(cfg),
    };

    private static string Describe(AppConfig cfg) => cfg.Backend.ToLowerInvariant() switch
    {
        "claude" => $"Claude Code CLI ({cfg.ClaudeModel})",
        "agy" or "gemini" => $"Gemini via agy ({cfg.AgyModel})",
        _ => AppConfig.ModelName(cfg.NimModel),
    };

    // ---- ISettingsHost -------------------------------------------------------------------------

    public AppConfig Config => _config;

    public void Apply(AppConfig cfg)
    {
        var old = _config;
        _config = cfg;
        _config.Save();
        _overlay.Style = StatusOverlay.Parse(_config.Indicator);
        if (old.Hotkey != cfg.Hotkey || old.RetryHotkey != cfg.RetryHotkey || old.SelectionHotkey != cfg.SelectionHotkey)
        {
            try { _capture.ReloadHotkey(); }
            catch (FormatException ex) { _tray.ShowBalloonTip(4000, "Inkling: bad shortcut", ex.Message, ToolTipIcon.Warning); }
        }
        // The backend reads the config it was created with, so replace it whenever anything it uses changes.
        // The API backend is free to recreate (shared connection); a CLI one is restarted only when something it uses changes.
        if (_backend is ChatApiBackend || old.Backend != cfg.Backend || old.NimModel != cfg.NimModel || old.NimBaseUrl != cfg.NimBaseUrl ||
            old.ClaudeModel != cfg.ClaudeModel || old.AgyModel != cfg.AgyModel || old.SingleLine != cfg.SingleLine ||
            old.AnswerLength != cfg.AnswerLength || old.SystemPrompt != cfg.SystemPrompt)
            ReplaceBackend();
        RefreshMenu();
    }

    public void SetHotkeysSuspended(bool suspended) => _capture.Suspended = suspended;

    public bool StartupEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunValue, false);
        }
    }

    public void ShowTimings()
    {
        if (_diagnostics is { IsDisposed: false }) { _diagnostics.Activate(); return; }
        _diagnostics = new DiagnosticsForm(() =>
            $"Using {Describe(_config)}, shortcut {_capture.Hotkey}, length {_config.AnswerLength}, " +
            $"typing {_config.TypingDelayMs} ms/key (slow apps {_config.SlowAppDelayMs})." + Environment.NewLine +
            "Times are per request; \"-\" means the step didn't happen.");
        _diagnostics.Show();
    }

    public void ShowWelcome()
    {
        if (_welcome != null) { _welcome.Activate(); return; }
        _welcome = new WelcomeWindow(this, ShowSettings);
        _welcome.Closed += (_, _) => _welcome = null;
        ShowWpf(_welcome);
    }

    public void ShowSettings()
    {
        if (_settings != null) { _settings.Activate(); return; }
        // Pick up hand edits to config.json made since the last time.
        Apply(AppConfig.Load());
        _settings = new SettingsWindow(this);
        _settings.Closed += (_, _) => _settings = null;
        ShowWpf(_settings);
    }

    private static void ShowWpf(System.Windows.Window window)
    {
        ElementHost.EnableModelessKeyboardInterop(window); // keyboard input in a WPF window run by a WinForms loop
        window.Show();
        window.Activate();
    }

    public void ClearCliContext()
    {
        _backend.ClearContext();
        _tray.ShowBalloonTip(1500, "Inkling", _backend is ChatApiBackend
            ? "Nothing to clear: every request to NVIDIA's API starts fresh."
            : $"{_backend.DisplayName}: context cleared, fresh CLI starting.", ToolTipIcon.Info);
    }

    /// <summary>For bug reports: settings and recent log lines. Never the key, prompts or answers.</summary>
    public string DiagnosticsText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Inkling {AppInfo.Version} on {Environment.OSVersion} ({(Environment.Is64BitProcess ? "x64" : "x86")})");
        sb.AppendLine($"Using: {Describe(_config)} [{_config.Backend}, {_config.NimModel}], API key: {Secrets.NimKeySource() ?? "none"}");
        sb.AppendLine($"Shortcuts: ask {_config.Hotkey}, selection {(_config.SelectionHotkey.Length > 0 ? _config.SelectionHotkey : "off")}, retry {(_config.RetryHotkey.Length > 0 ? _config.RetryHotkey : "off")}");
        sb.AppendLine($"Length {_config.AnswerLength}, single line {_config.SingleLine}, dots {_config.ThinkingDots}, indicator {_config.Indicator}, typing {_config.TypingDelayMs}/{_config.SlowAppDelayMs} ms, risk guard {_config.RiskGuard}, window titles {_config.SendWindowTitle}");
        sb.AppendLine($"Off in: {string.Join(", ", _config.DisabledApps)}; slow apps: {string.Join(", ", _config.SlowApps)}");
        sb.AppendLine(Diagnostics.Summary().Replace("\r\n", "\n").Trim());
        sb.AppendLine("--- last log lines ---");
        try { foreach (var line in File.ReadLines(Log.FilePath).TakeLast(40)) sb.AppendLine(line); } catch { }
        return sb.ToString();
    }

    // ---- Updates -------------------------------------------------------------------------------

    private async Task AutoCheckAsync()
    {
        if (!_config.CheckForUpdates || DateTime.UtcNow - _config.LastUpdateCheck < TimeSpan.FromHours(23)) return;
        try
        {
            var release = await Updates.CheckAsync();
            var cfg = _config.Clone();
            cfg.LastUpdateCheck = DateTime.UtcNow;
            Apply(cfg);
            if (release == null || release.Version.ToString(3) == _config.SkippedVersion) return;
            Offer(release);
            _tray.ShowBalloonTip(5000, $"Inkling {release.Version.ToString(3)} is available", "Click to see what's new and update.", ToolTipIcon.Info);
        }
        catch (Exception ex) { Log.Write("Update check failed: " + ex.Message); }
    }

    public async Task<string> CheckForUpdatesNowAsync()
    {
        try
        {
            var release = await Updates.CheckAsync();
            var cfg = _config.Clone();
            cfg.LastUpdateCheck = DateTime.UtcNow;
            Apply(cfg);
            if (release == null) return $"You're up to date ({AppInfo.Version}).";
            Offer(release);
            ShowUpdate();
            return $"Inkling {release.Version.ToString(3)} is available.";
        }
        catch (Exception ex)
        {
            Log.Write("Update check failed: " + ex.Message);
            return "Couldn't reach GitHub. Try again later.";
        }
    }

    private void Offer(Release release)
    {
        _available = release;
        _updateItem.Text = $"Update to {release.Version.ToString(3)}…";
        _updateItem.Font = new Font(_updateItem.Font, FontStyle.Bold);
        _updateItem.Visible = true;
    }

    private void ShowUpdate()
    {
        if (_available == null) return;
        if (_updateWindow != null) { _updateWindow.Activate(); return; }
        _updateWindow = new UpdateWindow(_available, _config.Theme, version =>
        {
            var cfg = _config.Clone();
            cfg.SkippedVersion = version;
            Apply(cfg);
            _updateItem.Visible = false;
        });
        _updateWindow.Closed += (_, _) => _updateWindow = null;
        ShowWpf(_updateWindow);
    }

    // ---- Tray menu -----------------------------------------------------------------------------

    private void ReplaceBackend()
    {
        var old = _backend;
        _backend = CreateBackend(_config);
        _backend.Warm();
        old.Dispose();
    }

    private void SetModel(string id)
    {
        var cfg = _config.Clone();
        cfg.NimModel = id;
        cfg.Backend = "nim";
        Apply(cfg);
    }

    private void SetLength(string length)
    {
        var cfg = _config.Clone();
        cfg.AnswerLength = length;
        Apply(cfg);
    }

    private void TogglePause()
    {
        _capture.Paused = !_capture.Paused;
        RefreshMenu();
    }

    private void RefreshMenu()
    {
        _modelMenu.DropDownItems.Clear();
        foreach (var m in AppConfig.Models)
        {
            var id = m.Id;
            _modelMenu.DropDownItems.Add(new ToolStripMenuItem(m.Name, null, (_, _) => SetModel(id))
            {
                Checked = _backend is ChatApiBackend && _config.NimModel == id,
                ToolTipText = m.Description,
            });
        }
        if (_backend is not ChatApiBackend)
            _modelMenu.DropDownItems.Add(new ToolStripMenuItem($"Using {Describe(_config)}") { Checked = true, Enabled = false });
        foreach (var (length, item) in _lengthItems) item.Checked = Prompt.NormalizeLength(_config.AnswerLength) == length;
        _pauseItem.Checked = _capture.Paused;

        var text = $"Inkling · {HotkeyBox.Pretty(_capture.Hotkey.ToString())} · {Describe(_config)}{(_capture.Paused ? " · paused" : "")}" +
                   (_backend is ChatApiBackend && Secrets.NimKeySource() == null ? " · no API key" : "");
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    /// <summary>Before the rename the Run value was "InlineAI" pointing at the old exe.</summary>
    private static void MigrateRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(OldRunValue) is not string) return;
            key.DeleteValue(OldRunValue, false);
            key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        }
        catch (Exception ex) { Log.Write("Run-key migration failed: " + ex.Message); }
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) File.WriteAllText(path, "");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write($"Could not open {path}: {ex.Message}"); }
    }

    /// <summary>The icon embedded in the exe (Assets\inkling.ico), at the small-icon size for the tray.</summary>
    private static Icon LoadIcon()
    {
        try
        {
            if (Icon.ExtractAssociatedIcon(Environment.ProcessPath!) is { } big)
                return new Icon(big, SystemInformation.SmallIconSize);
        }
        catch { }
        return SystemIcons.Application;
    }

    protected override void ExitThreadCore()
    {
        _updateTimer.Dispose();
        _updateWindow?.Close();
        _settings?.Close();
        _welcome?.Close();
        _tray.Visible = false;
        _capture.Dispose();
        _backend.Dispose();
        _overlay.Dispose();
        _diagnostics?.Dispose();
        _ui.Dispose();
        _tray.Dispose();
        base.ExitThreadCore();
    }
}
