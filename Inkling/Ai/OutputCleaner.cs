using System.Text.RegularExpressions;

namespace Inkling.Ai;

/// <summary>Strips the decoration models add despite instructions (fences, backticks, "$ " prompts).</summary>
public static partial class OutputCleaner
{
    /// <summary>
    /// The answer as it should be typed. With <paramref name="singleLine"/>, only its first line; otherwise every line,
    /// with indentation kept (code), blank lines inside kept, and any indentation shared by all lines removed.
    /// </summary>
    public static string Clean(string raw, bool singleLine)
    {
        var text = raw.Replace("\r\n", "\n").Trim('\n', '\r').TrimEnd();
        // Reasoning models put their thinking in <think>...</think> before the answer.
        var thinkEnd = text.LastIndexOf("</think>", StringComparison.Ordinal);
        if (thinkEnd >= 0) text = text[(thinkEnd + 8)..].Trim('\n', '\r').TrimEnd();
        else if (text.TrimStart().StartsWith("<think>", StringComparison.Ordinal)) return "";

        // Prefer the inside of a ``` fenced block if there is one (its first line's indentation matters for code).
        var fence = FenceRegex().Match(text);
        if (fence.Success) text = fence.Groups[1].Value.Trim('\n', '\r').TrimEnd();

        var lines = text.Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) return "";

        string result;
        if (singleLine || lines.Count == 1)
            result = CleanLine(lines[0]);
        else
        {
            // Every line a "$ cmd" prompt: strip them all. Otherwise leave the lines alone (code).
            if (lines.Where(l => l.Trim().Length > 0).All(l => PromptRegex().IsMatch(l.TrimStart())))
                lines = lines.Select(l => PromptRegex().Replace(l.TrimStart(), "")).ToList();
            result = string.Join("\n", Dedent(lines));
        }
        // Whole answer wrapped in matching quotes
        if (result.Length >= 2 && (result[0] == '"' && result[^1] == '"' || result[0] == '\'' && result[^1] == '\'')
            && result.IndexOf(result[0], 1) == result.Length - 1)
            result = result[1..^1];
        return result;
    }

    /// <summary>
    /// A rewritten selection: fences and thinking removed, but indentation kept exactly (the selection may sit inside
    /// indented code), and a final line break kept when the selection had one (whole lines were selected).
    /// </summary>
    public static string CleanRewrite(string raw, string selected)
    {
        var text = raw.Replace("\r\n", "\n");
        var thinkEnd = text.LastIndexOf("</think>", StringComparison.Ordinal);
        if (thinkEnd >= 0) text = text[(thinkEnd + 8)..];
        else if (text.TrimStart().StartsWith("<think>", StringComparison.Ordinal)) return "";
        // A fence only counts when the selection itself wasn't fenced Markdown.
        if (!selected.Contains("```") && FenceRegex().Match(text) is { Success: true } fence) text = fence.Groups[1].Value;
        // Some models echo the <<< >>> markers from the prompt.
        text = text.Trim('\n', '\r');
        if (text.StartsWith("<<<", StringComparison.Ordinal)) text = text[3..].TrimStart('\n', '\r');
        if (text.EndsWith(">>>", StringComparison.Ordinal)) text = text[..^3].TrimEnd('\n', '\r');
        text = string.Join("\n", text.Split('\n').Select(l => l.TrimEnd()));
        if (text.Trim().Length == 0) return "";
        // Quotes around the whole answer that the selection didn't have.
        var t = text.Trim();
        if (t.Length >= 2 && t[0] == '"' && t[^1] == '"' && t.IndexOf('"', 1) == t.Length - 1 && !selected.TrimStart().StartsWith('"'))
            text = t[1..^1];
        // Keep the selection's own leading indentation if the model dropped it.
        var lead = selected.Replace("\r\n", "\n");
        var indent = lead[..(lead.Length - lead.TrimStart(' ', '\t').Length)];
        if (indent.Length > 0 && !indent.Contains('\n') && !text.StartsWith(indent, StringComparison.Ordinal) && !char.IsWhiteSpace(text[0]))
            text = indent + text;
        if (selected.EndsWith('\n') && !text.EndsWith('\n')) text += "\n";
        return text;
    }

    /// <summary>
    /// What of an answer that is still arriving can be typed already. Null when it can't be streamed at all: it starts
    /// with a code fence, thinking, a quote or a "$ " prompt, which only the finished answer can clean. Trailing spaces
    /// and line breaks are held back until more text follows, since the finished answer is trimmed.
    /// </summary>
    public static string? LivePrefix(string raw, bool singleLine, bool done)
    {
        var t = raw.Replace("\r\n", "\n").TrimStart();
        if (t.Length == 0) return "";
        if (!done && t.Length < 3) return ""; // too early to tell "``" from "```"
        if (t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("<think", StringComparison.Ordinal)
            || t[0] is '"' or '\'' or '`' || PromptRegex().IsMatch(t))
            return null;
        if (singleLine && t.IndexOf('\n') is var nl and >= 0) t = t[..nl];
        return t.TrimEnd();
    }

    private static string CleanLine(string line)
    {
        var l = line.Trim();
        // `cmd`  → cmd
        if (l.Length >= 2 && l[0] == '`' && l[^1] == '`') l = l.Trim('`').Trim();
        // $ cmd / > cmd / PS C:\> cmd
        return PromptRegex().Replace(l, "");
    }

    /// <summary>Removes the indentation every non-blank line shares, keeping the rest.</summary>
    private static IEnumerable<string> Dedent(List<string> lines)
    {
        var indents = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart(' ', '\t').Length).ToList();
        int common = indents.Count == 0 ? 0 : indents.Min();
        return lines.Select(l => l.Trim().Length == 0 ? "" : l[Math.Min(common, l.Length)..]);
    }

    [GeneratedRegex(@"```[a-zA-Z0-9_+#-]*[ \t]*\n?(.*?)\n?[ \t]*```", RegexOptions.Singleline)]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^(\$|>|PS [^>]*>)\s+")]
    private static partial Regex PromptRegex();
}
