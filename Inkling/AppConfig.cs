using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inkling;

public sealed class ContextRule
{
    /// <summary>Process name without .exe, case-insensitive. Empty = any process.</summary>
    public string Process { get; set; } = "";
    /// <summary>Optional case-insensitive substring of the window title. Empty = any title.</summary>
    public string TitleContains { get; set; } = "";
    /// <summary>Optional extension of the open file (".ps1"). Rules with one are checked first. Empty = any file.</summary>
    public string Extension { get; set; } = "";
    /// <summary>Text added to the prompt describing what kind of answer fits here.</summary>
    public string Hint { get; set; } = "";
}

/// <summary>A sample request and the ideal answer, sent before the real request (few-shot) to set the style.</summary>
public sealed class ChatExample
{
    public string Hint { get; set; } = "";
    public string Request { get; set; } = "";
    public string Answer { get; set; } = "";
}

/// <summary>A model Inkling offers in Settings (picked by benchmark: fast and accurate for one-line answers).</summary>
public sealed record ModelChoice(string Id, string Name, string Description);

public sealed class AppConfig
{
    /// <summary>The models offered in Settings and the tray. See bench/results for how they were chosen.</summary>
    public static readonly ModelChoice[] Models =
    [
        new("nvidia/nemotron-3.5-bolt-e4b", "Nemotron 3.5 Bolt", "Fastest"),
        new("poolside/laguna-xs-2.1", "Laguna XS 2.1", "A little slower"),
    ];

    /// <summary>"system", "light" or "dark" for Inkling's windows.</summary>
    public string Theme { get; set; } = "system";
    /// <summary>False until the welcome window has been completed once.</summary>
    public bool FirstRunDone { get; set; }
    /// <summary>Show answers that look destructive (rm -rf, DROP TABLE, ...) first and wait for Enter before typing them.</summary>
    public bool RiskGuard { get; set; } = true;
    /// <summary>Requests typed exactly like a trigger get the saved text back instantly, without asking the model.</summary>
    public List<Snippet> Snippets { get; set; } =
    [
        new() { Trigger = "shrug", Text = @"¯\_(ツ)_/¯" },
        new() { Trigger = "today", Text = "{date}" },
    ];
    /// <summary>Look for a newer release on GitHub once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; }
    /// <summary>A release the user chose to skip: not offered again.</summary>
    public string SkippedVersion { get; set; } = "";
    /// <summary>Apps (process names) where Inkling's hotkeys do nothing and pass through: password managers, games, remote desktop...</summary>
    public List<string> DisabledApps { get; set; } = ["KeePass", "KeePassXC", "1Password", "Bitwarden"];

    public string Hotkey { get; set; } = "Ctrl+Alt+Space";
    /// <summary>Right after an answer is typed: erase it and ask again for a different one. Passed through to the app when there's nothing to retry.</summary>
    public string RetryHotkey { get; set; } = "Ctrl+Alt+R";
    /// <summary>Select text, press this: the selection is the request and the answer replaces it. Empty = off.</summary>
    public string SelectionHotkey { get; set; } = "Ctrl+Alt+W";
    /// <summary>Rewrite the selected text with an instruction you type ("make it formal"). Empty = off.</summary>
    public string RewriteHotkey { get; set; } = "Ctrl+Alt+Enter";
    /// <summary>Type longer answers (sentence, paragraph) while the model is still writing them.</summary>
    public bool StreamAnswers { get; set; } = true;
    public int MaxSelectionChars { get; set; } = 4000;
    /// <summary>Apps where select-to-ask is refused: terminals don't replace a selection when you type, and Ctrl+C can interrupt the shell.</summary>
    /// <summary>
    /// Editors with a built-in terminal that Inkling can't tell apart from the editor (same process): answers there stay
    /// on one line, code included, since several lines pasted into a shell could run. Terminals are always one line.
    /// </summary>
    public List<string> OneLineApps { get; set; } = ["Code", "Code - Insiders", "Cursor", "Windsurf", "VSCodium"];

    /// <summary>Whether answers in this app must stay on one line: a terminal, or an editor that may be showing one.</summary>
    public bool OneLineOnly(string process) =>
        SelectionSkipApps.Contains(process, StringComparer.OrdinalIgnoreCase) || OneLineApps.Contains(process, StringComparer.OrdinalIgnoreCase);

    public List<string> SelectionSkipApps { get; set; } =
        ["WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "wsl", "ubuntu", "mintty", "putty", "alacritty", "wezterm-gui", "WarpTerminal"];
    /// <summary>"nim" (NVIDIA's API), "local" (a model on this PC: Ollama, LM Studio), "claude" or "agy".</summary>
    public string Backend { get; set; } = "nim";

    /// <summary>Any OpenAI-compatible endpoint works (e.g. http://localhost:11434/v1 for Ollama). The key lives in Windows Credential Manager or NVIDIA_API_KEY.</summary>
    public string NimBaseUrl { get; set; } = "https://integrate.api.nvidia.com/v1";
    public string NimModel { get; set; } = "nvidia/nemotron-3.5-bolt-e4b";
    /// <summary>An OpenAI-compatible server on this PC. Ollama: http://localhost:11434/v1, LM Studio: http://localhost:1234/v1.</summary>
    public string LocalBaseUrl { get; set; } = "http://localhost:11434/v1";
    /// <summary>The local model's id as the server lists it ("llama3.1:latest"). Empty until picked in Settings.</summary>
    public string LocalModel { get; set; } = "";
    /// <summary>Client-side cap matching the key's rate limit, so a burst fails fast instead of waiting on a 429.</summary>
    public int NimRequestsPerMinute { get; set; } = 38;
    /// <summary>Few-shot examples for the API backend: how answers should look. Edit these to tune the style.</summary>
    public List<ChatExample> Examples { get; set; } =
    [
        new() { Hint = "a single bash command for Ubuntu running in WSL2", Request = "show disk usage of this folder sorted", Answer = "du -sh * | sort -h" },
        new() { Hint = "a single PowerShell command for Windows", Request = "list running services", Answer = "Get-Service | Where-Object Status -eq Running" },
        new() { Hint = "a short plain-text answer (one word or a short phrase if possible)", Request = "capital of australia", Answer = "Canberra" },
        new() { Hint = "a short code snippet or command", Request = "python read json file into dict", Answer = "data = json.load(open(\"file.json\"))" },
        new() { Hint = "a short plain-text answer (one word or a short phrase if possible)", Request = "thank you in japanese", Answer = "ありがとう (arigatou)" },
        new()
        {
            Hint = "a short code snippet or command", Request = "python function that checks if a number is prime",
            Answer = "def is_prime(n):\n    if n < 2:\n        return False\n    for i in range(2, int(n ** 0.5) + 1):\n        if n % i == 0:\n            return False\n    return True",
        },
        new()
        {
            Hint = "a short plain-text answer (one word or a short phrase if possible)", Request = "write a polite reply declining the meeting",
            Answer = "Thanks for the invite, but I won't be able to make it. Happy to catch up another time.",
        },
    ];

    public string ClaudeModel { get; set; } = "haiku";
    /// <summary>Optional full path to claude.exe / claude.cmd. Empty = find on PATH.</summary>
    public string ClaudePath { get; set; } = "";

    public string AgyModel { get; set; } = "gemini-3.8-flash-low";
    public string AgyPath { get; set; } = "";

    /// <summary>Requests one warm CLI process answers before a fresh one replaces it. 1 = every request is independent (no chat history); the next process is started in the background right away.</summary>
    public int RequestsPerProcess { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 45;
    /// <summary>How long a request may take. A local model may first have to load into memory (many seconds for a big
    /// one), so it gets at least two minutes.</summary>
    [JsonIgnore]
    public TimeSpan RequestTimeout => TimeSpan.FromSeconds(Backend.Equals("local", StringComparison.OrdinalIgnoreCase) ? Math.Max(TimeoutSeconds, 120) : TimeoutSeconds);
    /// <summary>Keep only the first line of the answer (recommended for terminals: a newline would run the command).</summary>
    public bool SingleLine { get; set; } = true;
    /// <summary>"short" (one word / command), "sentence" or "paragraph".</summary>
    public string AnswerLength { get; set; } = "short";
    /// <summary>"icon" = the small "i_rdy" badge in the screen's top-left corner; "bar" = text bar with instructions near the caret.</summary>
    public string Indicator { get; set; } = "icon";
    /// <summary>While waiting, replace the request in the text field with animated "..." (restored on cancel/error).</summary>
    public bool ThinkingDots { get; set; } = true;
    /// <summary>Include the target window's title in the context sent to the model (helps relevance; may contain private text).</summary>
    public bool SendWindowTitle { get; set; } = true;
    /// <summary>Include the page address in browsers (site and path only) or the open file's name elsewhere (no folder).
    /// Never in private windows.</summary>
    public bool SendOpenItem { get; set; } = true;
    /// <summary>Pause between typed keys, in ms. Lower is faster; too low and some apps (WinUI Notepad) mix up characters.</summary>
    public int TypingDelayMs { get; set; } = 5;
    /// <summary>Apps (process names) that drop fast injected keys, and the slower pace used for them.</summary>
    public List<string> SlowApps { get; set; } = ["Notepad"];
    public int SlowAppDelayMs { get; set; } = 30;

    public string SystemPrompt { get; set; } = DefaultSystemPrompt;

    public const string DefaultSystemPrompt =
        "You are an inline text replacer. The user's request will be replaced in place by your reply, " +
        "so reply with ONLY the answer itself, at the length asked for. " +
        "If the request asks you to write, create or do something (code, a message, a list, a translation), do it: " +
        "reply with the finished result itself, complete and working. Never describe how to do it, and never just repeat or name the task. " +
        "No explanation or preamble, no markdown, no code fences, no surrounding quotes, no leading '$'. Never use tools.";

    /// <summary>The 0.3-0.4.5 default, without the "do it" rule.</summary>
    private const string PreviousSystemPrompt =
        "You are an inline text replacer. The user's request will be replaced in place by your reply, " +
        "so reply with ONLY the answer itself, at the length asked for. " +
        "No explanation or preamble, no markdown, no code fences, no surrounding quotes, no leading '$'. Never use tools.";

    /// <summary>The v0.2 default, which fixed the length; replaced so the length setting can work.</summary>
    private const string OldSystemPrompt =
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
        new() { Process = "EXCEL", Hint = "a spreadsheet formula starting with = (or a short value)" },
        new() { Extension = ".ps1", Hint = "PowerShell code (a single command or a short snippet)" },
        new() { Extension = ".sh", Hint = "bash code (a single command or a short snippet)" },
        new() { Extension = ".py", Hint = "Python code (a short snippet)" },
        new() { Extension = ".sql", Hint = "a SQL query" },
        new() { Extension = ".csv", Hint = "comma-separated values that fit the file's columns" },
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
                if (loaded.SystemPrompt is OldSystemPrompt or PreviousSystemPrompt) loaded.SystemPrompt = DefaultSystemPrompt;
                // Before 0.4.7, Ctrl+Alt+Enter was the ask-about-selection default; it's now the rewrite one. A config
                // saved before the rewrite shortcut existed moves to the new defaults, so the two don't clash.
                if (!text.Contains("\"RewriteHotkey\"", StringComparison.Ordinal) && loaded.SelectionHotkey == "Ctrl+Alt+Enter")
                    loaded.SelectionHotkey = new AppConfig().SelectionHotkey;
                // Few-shot examples added in later versions (matched by request text).
                foreach (var ex in new AppConfig().Examples)
                    if (!loaded.Examples.Any(e => e.Request.Equals(ex.Request, StringComparison.OrdinalIgnoreCase)))
                        loaded.Examples.Add(ex);
                // Per-app hints added in later versions, for configs saved before they existed.
                foreach (var rule in new AppConfig().Contexts)
                    if (!loaded.Contexts.Any(c => c.Process.Equals(rule.Process, StringComparison.OrdinalIgnoreCase)
                                                  && c.Extension.Equals(rule.Extension, StringComparison.OrdinalIgnoreCase)))
                        loaded.Contexts.Add(rule);
                loaded.AnswerLength = Ai.Prompt.NormalizeLength(loaded.AnswerLength);
                // Add settings introduced by newer versions to the file, so they're visible when editing it.
                if (JsonSerializer.Serialize(loaded, Json) != text) loaded.Save();
                return loaded;
            }
            catch (Exception ex)
            {
                // Keep the unreadable file aside instead of overwriting it, so hand edits aren't lost.
                Log.Write($"config.json is invalid, using defaults: {ex.Message}");
                try { File.Copy(FilePath, Path.Combine(Dir, $"config.invalid-{DateTime.Now:yyyyMMdd-HHmmss}.json"), overwrite: true); } catch { }
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

    /// <summary>Atomic: written to a temp file first, so a crash or full disk never leaves a half-written config.</summary>
    public void Save()
    {
        lock (SaveGate)
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
    }

    private static readonly object SaveGate = new();

    /// <summary>An independent copy (Settings edits one, so Cancel/Reset are easy).</summary>
    public AppConfig Clone() => JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(this, Json), Json)!;

    /// <summary>A model's display name, or its id when it isn't one of <see cref="Models"/> (set by hand in config.json).</summary>
    public static string ModelName(string id) => Models.FirstOrDefault(m => m.Id == id)?.Name ?? id;
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
