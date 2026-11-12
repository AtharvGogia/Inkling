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
