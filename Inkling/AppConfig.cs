using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inkling;

public sealed class ContextRule
{
    /// <summary>Process name without .exe, case-insensitive. Empty = any process.</summary>
    public string Process { get; set; } = "";
    /// <summary>Optional case-insensitive substring of the window title. Empty = any title.</summary>
    public string TitleContains { get; set; } = "";
    /// <summary>Text added to the prompt describing what kind of answer fits here.</summary>
    public string Hint { get; set; } = "";
}

public sealed class AppConfig
{
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";
    /// <summary>"claude" or "agy".</summary>
    public string Backend { get; set; } = "claude";

    public string ClaudeModel { get; set; } = "haiku";
    /// <summary>Optional full path to claude.exe / claude.cmd. Empty = find on PATH.</summary>
    public string ClaudePath { get; set; } = "";

    public string AgyModel { get; set; } = "gemini-3.8-flash-low";
    public string AgyPath { get; set; } = "";

    /// <summary>Requests one warm CLI process answers before a fresh one replaces it. 1 = every request is independent (no chat history); the next process is started in the background right away.</summary>
    public int RequestsPerProcess { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 45;
    /// <summary>Keep only the first line of the answer (recommended for terminals: a newline would run the command).</summary>
    public bool SingleLine { get; set; } = true;
    /// <summary>"icon" = small light in the screen's top-left corner; "bar" = text bar with instructions near the caret.</summary>
    public string Indicator { get; set; } = "icon";
    /// <summary>While waiting, replace the request in the text field with animated ". . ." (restored on cancel/error).</summary>
    public bool ThinkingDots { get; set; } = true;
    /// <summary>Include the target window's title in the context sent to the model (helps relevance; may contain private text).</summary>
    public bool SendWindowTitle { get; set; } = true;
    /// <summary>Pause between typed keys, in ms. Lower is faster; too low and some apps (WinUI Notepad) mix up characters.</summary>
    public int TypingDelayMs { get; set; } = 5;
    /// <summary>Apps (process names) that drop fast injected keys, and the slower pace used for them.</summary>
    public List<string> SlowApps { get; set; } = ["Notepad"];
    public int SlowAppDelayMs { get; set; } = 30;

    public string SystemPrompt { get; set; } =
        "You are an inline text replacer. The user's request will be replaced in place by your reply, " +
        "so reply with ONLY the answer itself: a single command or a few words. " +
        "No explanation, no markdown, no code fences, no quotes, no leading '$'. Never use tools.";

    public string DefaultHint { get; set; } = "a short plain-text answer (one word or a short phrase if possible)";

    public List<ContextRule> Contexts { get; set; } =
    [
        new() { Process = "WindowsTerminal", TitleContains = "PowerShell", Hint = "a single PowerShell command for Windows" },
        new() { Process = "WindowsTerminal", TitleContains = "Command Prompt", Hint = "a single cmd.exe command for Windows" },
        new() { Process = "WindowsTerminal", Hint = "a single bash command for Ubuntu running in WSL2" },
        new() { Process = "wsl", Hint = "a single bash command for Ubuntu running in WSL2" },
        new() { Process = "ubuntu", Hint = "a single bash command for Ubuntu running in WSL2" },
        new() { Process = "pwsh", Hint = "a single PowerShell command for Windows" },
        new() { Process = "powershell", Hint = "a single PowerShell command for Windows" },
        new() { Process = "cmd", Hint = "a single cmd.exe command for Windows" },
        new() { Process = "Code", Hint = "a short code snippet or command" },
    ];

    [JsonIgnore]
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Inkling");
    [JsonIgnore]
    public static string FilePath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static AppConfig Load()
    {
        MigrateFromInlineAI();
        Directory.CreateDirectory(Dir);
        if (File.Exists(FilePath))
        {
            try
            {
                var text = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppConfig>(text, Json) ?? new AppConfig();
                // Add settings introduced by newer versions to the file, so they're visible when editing it.
                if (JsonSerializer.Serialize(loaded, Json) != text) loaded.Save();
                return loaded;
            }
            catch (Exception ex)
            {
                Log.Write($"config.json is invalid, using defaults: {ex.Message}");
                return new AppConfig();
            }
        }
        var cfg = new AppConfig();
        cfg.Save();
        return cfg;
    }

    /// <summary>The app was called "Inline AI" before v0.2: carry its settings over once.</summary>
    private static void MigrateFromInlineAI()
    {
        try
        {
            var old = Path.Combine(Path.GetDirectoryName(Dir)!, "InlineAI");
            if (!Directory.Exists(old) || Directory.Exists(Dir)) return;
            Directory.Move(old, Dir);
            var oldLog = Path.Combine(Dir, "inlineai.log");
            if (File.Exists(oldLog)) File.Move(oldLog, Log.FilePath, overwrite: true);
            Log.Write("Migrated settings from %APPDATA%\\InlineAI.");
        }
        catch (Exception ex) { Log.Write("Settings migration failed: " + ex.Message); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}

internal static class Log
{
    private static readonly object Gate = new();
    public static string FilePath => Path.Combine(AppConfig.Dir, "inkling.log");

    /// <summary>Diagnostic log. Never pass prompts or answers here: they may hold what the user typed.</summary>
    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }
}
