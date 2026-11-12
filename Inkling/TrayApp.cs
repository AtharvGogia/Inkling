using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Inkling.Ai;
using Inkling.Ui;
using Microsoft.Win32;

namespace Inkling;

/// <summary>Tray icon + menu; owns config, the active backend and the capture controller.</summary>
internal sealed class TrayApp : ApplicationContext
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
    private readonly ToolStripMenuItem _claudeItem, _agyItem, _pauseItem, _startupItem, _iconItem, _barItem;

    public TrayApp()
    {
        _config = AppConfig.Load();
        MigrateRunKey();
        _backend = CreateBackend(_config);
        _ui = new Control();
        _ui.CreateControl(); // a UI-thread handle for BeginInvoke from the hooks
        _ = _ui.Handle;
        _overlay = new StatusOverlay { Style = StatusOverlay.Parse(_config.Indicator) };

        _capture = new CaptureController(_ui, () => _config, () => _backend, _overlay);
        _backend.Warm();

        _claudeItem = new ToolStripMenuItem("", null, (_, _) => SwitchBackend("claude"));
        _agyItem = new ToolStripMenuItem("", null, (_, _) => SwitchBackend("agy"));
        _iconItem = new ToolStripMenuItem("Small icon (top-left of screen)", null, (_, _) => SetIndicator(IndicatorStyle.Icon));
        _barItem = new ToolStripMenuItem("Full bar with instructions", null, (_, _) => SetIndicator(IndicatorStyle.Bar));
        _pauseItem = new ToolStripMenuItem("Pause", null, (_, _) => TogglePause());
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup()) { Checked = IsStartupEnabled() };

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Backend", null, _claudeItem, _agyItem));
        menu.Items.Add(new ToolStripMenuItem("Indicator", null, _iconItem, _barItem));
        menu.Items.Add("Clear AI context (fresh CLI)", null, (_, _) =>
        {
            _backend.ClearContext();
            _tray?.ShowBalloonTip(1500, "Inkling", $"{_backend.DisplayName}: context cleared, fresh CLI starting.", ToolTipIcon.Info);
        });
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Edit config", null, (_, _) => OpenFile(AppConfig.FilePath));
        menu.Items.Add("Reload config", null, (_, _) => ReloadConfig());
        menu.Items.Add("Open log", null, (_, _) => OpenFile(Log.FilePath));
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _tray = new NotifyIcon { Icon = LoadIcon(), ContextMenuStrip = menu, Visible = true };
        _overlay.Message += (text, isError) =>
            _tray.ShowBalloonTip(isError ? 4000 : 2000, "Inkling", text, isError ? ToolTipIcon.Warning : ToolTipIcon.Info);
        RefreshMenu();
        _tray.ShowBalloonTip(2500, "Inkling is running",
            $"Press {_capture.Hotkey}, type a request, press Enter. Using {_backend.DisplayName}.", ToolTipIcon.Info);
    }

    private static IAiBackend CreateBackend(AppConfig cfg) =>
        cfg.Backend.Equals("agy", StringComparison.OrdinalIgnoreCase) || cfg.Backend.Equals("gemini", StringComparison.OrdinalIgnoreCase)
            ? new AgyBackend(cfg)
            : new ClaudeBackend(cfg);

    private void SwitchBackend(string name)
    {
        _config.Backend = name;
        _config.Save();
        ReplaceBackend();
    }

    private void ReplaceBackend()
    {
        var old = _backend;
        _backend = CreateBackend(_config);
        _backend.Warm();
        old.Dispose();
        RefreshMenu();
    }

    private void ReloadConfig()
    {
        _config = AppConfig.Load();
        _overlay.Style = StatusOverlay.Parse(_config.Indicator);
        try { _capture.ReloadHotkey(); }
        catch (FormatException ex)
        {
            _tray.ShowBalloonTip(4000, "Inkling: bad hotkey", ex.Message, ToolTipIcon.Warning);
        }
        ReplaceBackend();
        _tray.ShowBalloonTip(1500, "Inkling", $"Config reloaded. Hotkey {_capture.Hotkey}, {_backend.DisplayName}.", ToolTipIcon.Info);
    }

    private void SetIndicator(IndicatorStyle style)
    {
        _overlay.HideNow();
        _overlay.Style = style;
        _config.Indicator = style == IndicatorStyle.Bar ? "bar" : "icon";
        _config.Save();
        RefreshMenu();
    }

    private void TogglePause()
    {
        _capture.Paused = !_capture.Paused;
        RefreshMenu();
    }

    private void RefreshMenu()
    {
        _claudeItem.Text = $"Claude ({_config.ClaudeModel})";
        _agyItem.Text = $"Gemini via agy ({_config.AgyModel})";
        _claudeItem.Checked = _backend is ClaudeBackend;
        _agyItem.Checked = _backend is AgyBackend;
        _pauseItem.Checked = _capture.Paused;
        _iconItem.Checked = _overlay.Style == IndicatorStyle.Icon;
        _barItem.Checked = _overlay.Style == IndicatorStyle.Bar;
        var text = $"Inkling · {_capture.Hotkey} · {_backend.DisplayName}{(_capture.Paused ? " · paused" : "")}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string;
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

    private void ToggleStartup()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (IsStartupEnabled()) key.DeleteValue(RunValue, false);
        else key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        _startupItem.Checked = IsStartupEnabled();
    }

    private static void OpenFile(string path)
    {
        if (!File.Exists(path)) File.WriteAllText(path, "");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
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
        _tray.Visible = false;
        _capture.Dispose();
        _backend.Dispose();
        _overlay.Dispose();
        _ui.Dispose();
        _tray.Dispose();
        base.ExitThreadCore();
    }
}
