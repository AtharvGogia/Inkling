using System.Globalization;

namespace Inkling;

/// <summary>A fixed reply for a request typed exactly as <see cref="Trigger"/>: no model call, instant.</summary>
public sealed class Snippet
{
    public string Trigger { get; set; } = "";
    public string Text { get; set; } = "";
}

internal static class Snippets
{
    /// <summary>The snippet text for this request (placeholders filled in), or null when no trigger matches.</summary>
    public static string? Expand(AppConfig cfg, string request)
    {
        var key = Normalize(request);
        if (key.Length == 0) return null;
        var hit = cfg.Snippets.FirstOrDefault(s => s.Trigger.Trim().Length > 0 && Normalize(s.Trigger) == key);
        return hit == null ? null : Fill(hit.Text);
    }

    private static string Normalize(string s) => string.Join(' ', s.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>{date} 2026-11-13, {time} 14:05, {datetime}, {clipboard}.</summary>
    public static string Fill(string text)
    {
        if (!text.Contains('{')) return text;
        var now = DateTime.Now;
        text = text.Replace("{date}", now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                   .Replace("{time}", now.ToString("HH:mm", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
                   .Replace("{datetime}", now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        if (text.Contains("{clipboard}", StringComparison.OrdinalIgnoreCase))
        {
            string clip = "";
            try { clip = Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { /* clipboard busy */ }
            text = text.Replace("{clipboard}", clip, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }
}
