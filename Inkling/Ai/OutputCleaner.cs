using System.Text.RegularExpressions;

namespace Inkling.Ai;

/// <summary>Strips the decoration models add despite instructions (fences, backticks, "$ " prompts).</summary>
public static partial class OutputCleaner
{
    public static string Clean(string raw, bool singleLine)
    {
        var text = raw.Replace("\r\n", "\n").Trim();
        // Reasoning models put their thinking in <think>...</think> before the answer.
        var thinkEnd = text.LastIndexOf("</think>", StringComparison.Ordinal);
        if (thinkEnd >= 0) text = text[(thinkEnd + 8)..].Trim();
        else if (text.StartsWith("<think>", StringComparison.Ordinal)) return "";

        // Prefer the inside of a ``` fenced block if there is one.
        var fence = FenceRegex().Match(text);
        if (fence.Success) text = fence.Groups[1].Value.Trim();

        var lines = text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return "";
        if (singleLine) lines = [lines[0]];

        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i].Trim();
            // `cmd`  → cmd
            if (l.Length >= 2 && l[0] == '`' && l[^1] == '`') l = l.Trim('`').Trim();
            // $ cmd / > cmd / PS C:\> cmd
            l = PromptRegex().Replace(l, "");
            lines[i] = l;
        }
        var result = string.Join("\n", lines).Trim();
        // Whole answer wrapped in matching quotes
        if (result.Length >= 2 && (result[0] == '"' && result[^1] == '"' || result[0] == '\'' && result[^1] == '\'')
            && result.IndexOf(result[0], 1) == result.Length - 1)
            result = result[1..^1];
        return result;
    }

    [GeneratedRegex(@"```[a-zA-Z0-9_+-]*\s*\n?(.*?)\n?```", RegexOptions.Singleline)]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"^(\$|>|PS [^>]*>)\s+")]
    private static partial Regex PromptRegex();
}
