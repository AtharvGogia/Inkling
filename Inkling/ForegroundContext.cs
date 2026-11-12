using System.Diagnostics;
using static Inkling.Native;

namespace Inkling;

/// <summary>
/// Describes where the answer will be typed: which app, which window, and what kind of answer fits
/// there ("bash command for WSL", "PowerShell command", ...), so the model can tailor its reply.
/// </summary>
internal static class ForegroundContext
{
    public static string ProcessName(nint hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return ""; } // process gone or access denied
    }

    /// <summary>"Windows Terminal", "Google Chrome", "Visual Studio Code"... falls back to the process name.</summary>
    private static string FriendlyName(nint hwnd, string process)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            using var p = Process.GetProcessById((int)pid);
            var desc = p.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
        }
        catch { /* elevated or protected process */ }
        return process;
    }

    /// <summary>The first matching rule's hint. Rules for the open file's extension win over app-only rules.</summary>
    public static string HintFor(string process, string title, AppConfig cfg, string extension = "")
    {
        foreach (var byExtension in new[] { true, false })
            foreach (var rule in cfg.Contexts)
            {
                if ((rule.Extension.Length > 0) != byExtension) continue;
                bool extOk = rule.Extension.Length == 0 || string.Equals(rule.Extension, extension, StringComparison.OrdinalIgnoreCase);
                bool procOk = rule.Process.Length == 0 || string.Equals(rule.Process, process, StringComparison.OrdinalIgnoreCase);
                bool titleOk = rule.TitleContains.Length == 0 || title.Contains(rule.TitleContains, StringComparison.OrdinalIgnoreCase);
                if (extOk && procOk && titleOk && rule.Hint.Length > 0) return rule.Hint;
            }
        return cfg.DefaultHint;
    }

    /// <summary>Where the answer goes: the app, its window title, and the page or file that's open (if allowed).</summary>
    public sealed record Where(string App, string Process, string Title, string? Url, OpenFile.Result? File);

    /// <summary>Reads <see cref="Where"/> for a window. <paramref name="url"/> comes from <see cref="BrowserUrl"/>;
    /// the open file's name is read from the title here.</summary>
    public static Where Read(nint hwnd, AppConfig cfg, string? url = null)
    {
        var process = ProcessName(hwnd);
        var title = GetWindowTitle(hwnd).Trim();
        var file = cfg.SendOpenItem && !BrowserUrl.IsBrowser(process) && !BrowserUrl.IsPrivate(title) ? OpenFile.FromTitle(process, title) : null;
        return new Where(FriendlyName(hwnd, process), process, title, cfg.SendOpenItem ? url : null, file);
    }

    /// <summary>The context block sent to the model with every request.</summary>
    public static string Describe(nint hwnd, AppConfig cfg, string? url = null) => Compose(Read(hwnd, cfg, url), cfg);

    public static string Compose(Where w, AppConfig cfg) => Compose(w.App, w.Process, w.Title, cfg, w.Url, w.File);

    /// <summary>The context block for a given app, process and window title (also used by --bench).</summary>
    public static string Compose(string app, string process, string title, AppConfig cfg, string? url = null, OpenFile.Result? file = null)
    {
        var hint = HintFor(process, title, cfg, file?.Extension ?? "");

        var where = process.Length > 0 ? $"{app} ({process}.exe)" : "an unknown application";
        if (cfg.SendWindowTitle && title.Length > 0)
            where += $", window title \"{(title.Length > 200 ? title[..200] + "…" : title)}\"";
        if (file != null) where += ", " + file.Describe();
        if (url != null) where += $", page \"{url}\"";

        return
            $"Context: the user is typing in {where}. Your reply is typed straight into that app at the cursor, " +
            "replacing the request, so it must fit there as-is. Use the context to make it relevant: e.g. the shell " +
            "and current folder from a terminal title, the language/framework from the open file's name, the site and page " +
            "from a browser's address.\n" +
            $"Answer type wanted here: {hint}.";
    }
}
