namespace Inkling.Ai;

/// <summary>Builds the user message sent to the model: where the answer goes, how long it should be, the request.</summary>
public static class Prompt
{
    public static readonly string[] Lengths = ["short", "sentence", "paragraph"];

    public static string NormalizeLength(string? s) =>
        Lengths.FirstOrDefault(l => l.Equals(s?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "short";

    public static string LengthLabel(string length) => NormalizeLength(length) switch
    {
        "sentence" => "A sentence or two",
        "paragraph" => "A paragraph",
        _ => "Short: a word or a command",
    };

    internal static string LengthInstruction(AppConfig cfg, CodeIntent? code = null, bool terminal = false)
    {
        var length = NormalizeLength(cfg.AnswerLength);
        // Code outside a terminal: as many lines as it takes, whatever the length setting says about prose.
        if (code != null && !terminal)
            return "write the actual code, complete and working: the shortest version that does the whole task, on as many lines " +
                   "as it needs, with normal indentation, and nothing before or after it (no explanation, no \"here is\")" + length switch
                   {
                       "paragraph" => ", with brief comments where they help",
                       "sentence" => "",
                       _ => ", only the essential lines",
                   };
        if (code != null) return "a single command line (a one-liner if code is needed), not a description of how to do it";
        return length switch
        {
            "sentence" => "one or two sentences (or a single command line if a command is what fits)",
            "paragraph" => "a short paragraph of at most about 80 words",
            _ => "as short as possible while still doing what is asked: one word, one command or a few words",
        } + (cfg.SingleLine ? ", all on one line" : "");
    }

    /// <summary>Instructions picked with 1-5 when rewriting a selection, so the common ones need no typing.</summary>
    public static readonly (string Label, string Instruction)[] RewritePresets =
    [
        ("fix grammar", "Fix the spelling and grammar, and change nothing else"),
        ("shorter", "Make it shorter, keeping the meaning"),
        ("formal", "Make it more formal"),
        ("friendlier", "Make it friendlier and more casual"),
        ("bullets", "Turn it into a short bulleted list"),
    ];

    /// <summary>The message for rewriting selected text with an instruction ("make it formal").</summary>
    public static string BuildRewrite(string context, string selected, string instruction) =>
        string.Join("\n",
            context,
            "Task: rewrite the selected text below as the instruction says. Reply with only the new text that will replace " +
            "the selection: in the same language as the text unless the instruction says otherwise, keeping its line breaks, " +
            "formatting and indentation where they still fit, and with nothing before or after it (no quotes, no \"Here is\", no notes).",
            "Instruction: " + instruction,
            "Selected text:",
            "<<<",
            selected,
            ">>>");

    /// <summary>The message for one request. <paramref name="code"/> is set when the request wants code written;
    /// <paramref name="terminal"/> keeps a code answer to one line, since Enter would run it.</summary>
    public static string Build(AppConfig cfg, string context, string request, IReadOnlyList<string> rejected,
        CodeIntent? code = null, bool terminal = false)
    {
        var parts = new List<string> { context };
        if (code?.Describe() is { } lang) parts.Add($"Code language here: {lang}. Use it unless the request names another.");
        parts.Add($"Length wanted: {LengthInstruction(cfg, code, terminal)}.");
        parts.Add("Request: " + request);
        if (rejected.Count > 0)
            parts.Add("The user rejected these earlier answers. Give a different, better one (not a repeat):\n" +
                      string.Join("\n", rejected.Select(a => "- " + a)));
        return string.Join("\n", parts);
    }
}
