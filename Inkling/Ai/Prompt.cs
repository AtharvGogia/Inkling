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

    private static string LengthInstruction(AppConfig cfg) => NormalizeLength(cfg.AnswerLength) switch
    {
        "sentence" => "one or two sentences (or a single command line if a command is what fits)",
        "paragraph" => "a short paragraph of at most about 80 words",
        _ => "as short as possible: one word, one command or a few words",
    } + (cfg.SingleLine ? ", all on one line" : "");

    public static string Build(AppConfig cfg, string context, string request, IReadOnlyList<string> rejected)
    {
        var parts = new List<string>
        {
            context,
            $"Length wanted: {LengthInstruction(cfg)}.",
        };
        parts.Add("Request: " + request);
        if (rejected.Count > 0)
            parts.Add("The user rejected these earlier answers. Give a different, better one (not a repeat):\n" +
                      string.Join("\n", rejected.Select(a => "- " + a)));
        return string.Join("\n", parts);
    }
}
