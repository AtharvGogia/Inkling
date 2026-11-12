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

    public static string HintFor(string process, string title, AppConfig cfg)
    {
        foreach (var rule in cfg.Contexts)
        {
            bool procOk = rule.Process.Length == 0 || string.Equals(rule.Process, process, StringComparison.OrdinalIgnoreCase);
            bool titleOk = rule.TitleContains.Length == 0 || title.Contains(rule.TitleContains, StringComparison.OrdinalIgnoreCase);
            if (procOk && titleOk && rule.Hint.Length > 0) return rule.Hint;
        }
        return cfg.DefaultHint;
    }

    /// <summary>The context block sent to the model with every request.</summary>
    public static string Describe(nint hwnd, AppConfig cfg)
    {
        var process = ProcessName(hwnd);
        return Compose(FriendlyName(hwnd, process), process, GetWindowTitle(hwnd).Trim(), cfg);
    }

    /// <summary>The context block for a given app, process and window title (also used by --bench).</summary>
    public static string Compose(string app, string process, string title, AppConfig cfg)
    {
        var hint = HintFor(process, title, cfg);

        var where = process.Length > 0 ? $"{app} ({process}.exe)" : "an unknown application";
        if (cfg.SendWindowTitle && title.Length > 0)
            where += $", window title \"{(title.Length > 200 ? title[..200] + "…" : title)}\"";

        return
            $"Context: the user is typing in {where}. Your reply is typed straight into that app at the cursor, " +
            "replacing the request, so it must fit there as-is. Use the context to make it relevant: e.g. the shell " +
            "and current folder from a terminal title, the language/framework from an editor's file name, the site " +
            "from a browser title.\n" +
            $"Answer type wanted here: {hint}.";
    }
}
