using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inkling.Ai;

namespace Inkling;

/// <summary>
/// Benchmark through the real request path (system prompt, examples, app context, streaming, answer cleaning):
///   Inkling.exe --bench [--questions bench\questions.json] [--out bench\results] model1 [model2 ...]
/// A model is a NIM model id, or "claude" / "agy" for the CLI backends. Writes a Markdown report.
/// </summary>
internal static class Bench
{
    private sealed record App(string Name, string Process, string Title);
    private sealed record Question(string Domain, string In, string Q, string[] Expect, string[] Require, int MaxChars);
    private sealed record Result(string Model, Question Q, string Answer, bool Correct, bool FormatOk, string? Error,
        double FirstMs, double TotalMs, int ReasoningChars);

    public static int Run(string[] args)
    {
        string questionsPath = Path.Combine("bench", "questions.json");
        string outDir = Path.Combine("bench", "results");
        var models = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--questions" && i + 1 < args.Length) questionsPath = args[++i];
            else if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
            else models.Add(args[i]);
        }
        if (models.Count == 0) { Console.WriteLine("Usage: --bench [--questions file] [--out dir] model1 [model2 ...]"); return 2; }

        var (apps, questions) = Load(questionsPath);
        var baseCfg = AppConfig.Load();
        var results = new List<Result>();

        foreach (var model in models)
        {
            var cfg = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(baseCfg))!; // independent copy
            cfg.SingleLine = true;
            cfg.AnswerLength = "short";
            if (model is "claude" or "agy") cfg.Backend = model;
            else { cfg.Backend = "nim"; cfg.NimModel = model; }
            bool api = cfg.Backend == "nim";

            Console.WriteLine($"\n== {model} ({questions.Count} questions)");
            using var backend = TrayApp.CreateBackend(cfg);
            backend.Warm();
            Thread.Sleep(api ? 1500 : 6000);

            foreach (var q in questions)
            {
                var start = Environment.TickCount64;
                var r = Ask(backend, cfg, model, q, apps[q.In]);
                results.Add(r);
                Console.WriteLine($"  {(r.Error != null ? "ERR" : r.Correct ? " ok" : "BAD")} {r.TotalMs,6:0} ms  {q.Domain,-11} {q.Q,-45} -> {Short(r.Error ?? r.Answer, 70)}");
                // Stay under the key's ~40 requests/min (NIM); give the CLI time to warm its replacement process.
                var minGap = api ? 2000 : 2500;
                var left = minGap - (Environment.TickCount64 - start);
                if (left > 0) Thread.Sleep((int)left);
            }
        }

        Directory.CreateDirectory(outDir);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);
        var mdPath = Path.Combine(outDir, $"bench-{stamp}.md");
        File.WriteAllText(mdPath, Report(results, models, questions));
        File.WriteAllText(Path.Combine(outDir, $"bench-{stamp}.json"), JsonSerializer.Serialize(results.Select(r => new
        {
            r.Model, r.Q.Domain, Question = r.Q.Q, r.Answer, r.Correct, r.FormatOk, r.Error, r.FirstMs, r.TotalMs, r.ReasoningChars,
        }), new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Console.WriteLine("\n" + Summary(results, models));
        Console.WriteLine($"Report: {Path.GetFullPath(mdPath)}");
        return 0;
    }

    private static Result Ask(IAiBackend backend, AppConfig cfg, string model, Question q, App app)
    {
        var trace = new RequestTrace();
        var message = Prompt.Build(cfg, ForegroundContext.Compose(app.Name, app.Process, app.Title, cfg), q.Q, []);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var raw = backend.AskAsync(message, trace, cts.Token).GetAwaiter().GetResult();
            var answer = OutputCleaner.Clean(raw, singleLine: true);
            double total = trace.ResultMs;
            bool correct = answer.Length > 0
                && (q.Expect.Length == 0 || q.Expect.Any(p => Regex.IsMatch(answer, p, RegexOptions.IgnoreCase)))
                && q.Require.All(p => Regex.IsMatch(answer, p, RegexOptions.IgnoreCase));
            bool formatOk = answer.Length > 0 && answer.Length <= q.MaxChars && !raw.Contains("```")
                && !Regex.IsMatch(answer, @"^(sure|here|certainly|the answer|to \w+ )", RegexOptions.IgnoreCase);
            return new Result(model, q, answer, correct, formatOk, answer.Length == 0 ? "empty answer" : null,
                trace.FirstOutputMs, total, trace.ReasoningChars);
        }
        catch (Exception ex)
        {
            var msg = ex is OperationCanceledException ? "timed out (45 s)" : ex.Message;
            return new Result(model, q, "", false, false, msg, trace.FirstOutputMs, trace.Now, trace.ReasoningChars);
        }
    }

    private static (Dictionary<string, App>, List<Question>) Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var apps = doc.RootElement.GetProperty("apps").EnumerateObject().ToDictionary(
            p => p.Name,
            p => new App(p.Value.GetProperty("app").GetString()!, p.Value.GetProperty("process").GetString()!, p.Value.GetProperty("title").GetString()!));
        static string[] Arr(JsonElement e, string name) =>
            e.TryGetProperty(name, out var a) ? a.EnumerateArray().Select(x => x.GetString()!).ToArray() : [];
        var qs = doc.RootElement.GetProperty("questions").EnumerateArray().Select(e => new Question(
            e.GetProperty("domain").GetString()!, e.GetProperty("in").GetString()!, e.GetProperty("q").GetString()!,
            Arr(e, "expect"), Arr(e, "require"),
            e.TryGetProperty("maxChars", out var m) ? m.GetInt32() : 150)).ToList();
        return (apps, qs);
    }

    private static double Pct(int n, int d) => d == 0 ? 0 : 100.0 * n / d;

    private static double Quantile(IEnumerable<double> xs, double q)
    {
        var v = xs.Where(x => x >= 0).OrderBy(x => x).ToList();
        if (v.Count == 0) return -1;
        var pos = (v.Count - 1) * q;
        int lo = (int)Math.Floor(pos), hi = (int)Math.Ceiling(pos);
        return v[lo] + (v[hi] - v[lo]) * (pos - lo);
    }

    private static string Ms(double v) => v < 0 ? "-" : v.ToString("0", CultureInfo.InvariantCulture);

    private static string Summary(List<Result> results, List<string> models)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Model | Correct | Clean format | Errors | Median first token | Median total | p90 total | Max total |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var m in models)
        {
            var rs = results.Where(r => r.Model == m).ToList();
            var ok = rs.Where(r => r.Error == null).ToList();
            sb.AppendLine($"| `{m}` | **{Pct(rs.Count(r => r.Correct), rs.Count):0}%** ({rs.Count(r => r.Correct)}/{rs.Count}) | " +
                          $"{Pct(rs.Count(r => r.FormatOk), rs.Count):0}% | {rs.Count(r => r.Error != null)} | " +
                          $"{Ms(Quantile(ok.Select(r => r.FirstMs), 0.5))} ms | **{Ms(Quantile(ok.Select(r => r.TotalMs), 0.5))} ms** | " +
                          $"{Ms(Quantile(ok.Select(r => r.TotalMs), 0.9))} ms | {Ms(ok.Count == 0 ? -1 : ok.Max(r => r.TotalMs))} ms |");
        }
        return sb.ToString();
    }

    private static string Report(List<Result> results, List<string> models, List<Question> questions)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Inkling benchmark, {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine($"{questions.Count} questions in {questions.Select(q => q.Domain).Distinct().Count()} domains, asked once per model through Inkling's real request path " +
                      "(system prompt, few-shot examples, app context, streaming with first-line stop, answer cleaning), answer length *short*. " +
                      "Times are request sent → answer ready, with a warm connection or CLI. Correct = matches the question's regex grader in `bench/questions.json`; " +
                      "clean format = non-empty, at most 150 characters, no code fences or preamble.");
        sb.AppendLine();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.Append(Summary(results, models));
        sb.AppendLine();
        sb.AppendLine("## Accuracy by domain");
        sb.AppendLine();
        sb.AppendLine("| Domain | " + string.Join(" | ", models.Select(m => $"`{ShortModel(m)}`")) + " |");
        sb.AppendLine("|---|" + string.Concat(models.Select(_ => "---|")));
        foreach (var d in questions.Select(q => q.Domain).Distinct())
        {
            sb.Append($"| {d} |");
            foreach (var m in models)
            {
                var rs = results.Where(r => r.Model == m && r.Q.Domain == d).ToList();
                sb.Append($" {rs.Count(r => r.Correct)}/{rs.Count} |");
            }
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("## Every answer");
        sb.AppendLine();
        sb.AppendLine("✓ correct · ✗ wrong · ⚠ error. Time in ms.");
        sb.AppendLine();
        sb.AppendLine("| Domain | Request | " + string.Join(" | ", models.Select(m => $"`{ShortModel(m)}`")) + " |");
        sb.AppendLine("|---|---|" + string.Concat(models.Select(_ => "---|")));
        foreach (var q in questions)
        {
            sb.Append($"| {q.Domain} | {Cell(q.Q)} |");
            foreach (var m in models)
            {
                var r = results.First(x => x.Model == m && x.Q == q);
                var mark = r.Error != null ? "⚠" : r.Correct ? "✓" : "✗";
                sb.Append($" {mark} {Cell(Short(r.Error ?? r.Answer, 60))} <sub>{Ms(r.TotalMs)}</sub> |");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string ShortModel(string m) => m.Contains('/') ? m[(m.LastIndexOf('/') + 1)..] : m;
    private static string Short(string s, int n) => s.Length > n ? s[..(n - 1)] + "…" : s;
    private static string Cell(string s) => "`" + s.Replace("|", "\\|").Replace("`", "'") + "`";
}
