using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Inkling;

/// <summary>
/// Timing breakdown of one request, from Enter to the last typed key. Holds no prompt or
/// answer text, only lengths and timings, so it is safe to log and to show.
/// All times are in milliseconds; -1 = didn't happen.
/// </summary>
public sealed class RequestTrace
{
    private readonly long _start = Stopwatch.GetTimestamp();

    public DateTime At { get; } = DateTime.Now;
    public string Backend { get; set; } = "";
    public string App { get; set; } = "";
    public string Length { get; set; } = "";
    public bool Retry { get; set; }
    public string Outcome { get; set; } = "";

    // Before the request is sent
    /// <summary>Select-to-ask: hotkey -> selected text copied (the clipboard is restored after).</summary>
    public double SelectionMs { get; set; } = -1;
    public int SelectionChars { get; set; }
    /// <summary>Reading the app name, window title and hint.</summary>
    public double ContextMs { get; set; }
    /// <summary>Waiting for the browser's page address after Enter (-1 = not a lookup).</summary>
    public double UrlMs { get; set; } = -1;
    public bool UrlFound { get; set; }
    public bool UrlTimedOut { get; set; }
    public int PromptChars { get; set; }

    // CLI
    /// <summary>Waiting for the CLI to be free (another request or a warm-up in progress).</summary>
    public double GateWaitMs { get; set; }
    /// <summary>No warm CLI was ready, so one was started for this request.</summary>
    public bool ColdStart { get; set; }
    /// <summary>Process.Start of the CLI that answered (done in the background unless <see cref="ColdStart"/>).</summary>
    public double SpawnMs { get; set; } = -1;
    /// <summary>How long that CLI had been running when the request was written to it.</summary>
    public double ProcessAgeMs { get; set; } = -1;
    /// <summary>Spawn -> first line the CLI printed on its own, before any request (its own startup), if it prints one.</summary>
    public double StartupOutputMs { get; set; } = -1;
    /// <summary>Request written -> first line back.</summary>
    public double FirstOutputMs { get; set; } = -1;
    /// <summary>Request written -> result line: the whole turn as seen from Inkling.</summary>
    public double ResultMs { get; set; } = -1;
    /// <summary>The CLI's own total for the turn, if it reports one (Claude: duration_ms).</summary>
    public double CliReportedMs { get; set; } = -1;
    /// <summary>Time the CLI says it spent waiting on the model API (Claude: duration_api_ms).</summary>
    public double ApiMs { get; set; } = -1;
    public string Tokens { get; set; } = "";
    /// <summary>Hidden "thinking" text streamed by reasoning models before the answer (API backend).</summary>
    public int ReasoningChars { get; set; }
    public double ReasoningStartMs { get; set; } = -1;
    public int OutputLines { get; set; }

    // After the answer arrives
    /// <summary>JSON parsing of all CLI output lines plus cleaning the answer.</summary>
    public double ParseMs { get; set; }
    /// <summary>Backspacing the request away to show the dots (overlaps the model wait).</summary>
    public double EraseRequestMs { get; set; } = -1;
    /// <summary>Answer arrived -> dots animation stopped.</summary>
    public double DotsStopMs { get; set; } = -1;
    public double FinalEraseMs { get; set; } = -1;
    public double TypeMs { get; set; } = -1;
    public int TypedChars { get; set; }
    public int PaceMs { get; set; }

    /// <summary>Enter -> request written to the CLI.</summary>
    public double SentAtMs { get; set; } = -1;
    /// <summary>Enter -> answer received from the CLI.</summary>
    public double AnswerAtMs { get; set; } = -1;
    /// <summary>Enter -> done (last key typed, or failure shown).</summary>
    public double TotalMs { get; set; } = -1;

    /// <summary>Milliseconds since this trace was created (i.e. since Enter).</summary>
    public double Now => Stopwatch.GetElapsedTime(_start).TotalMilliseconds;

    public static double Since(long timestamp) => Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;

    private static string Ms(double v) => v < 0 ? "-" : v < 10 ? v.ToString("0.0", CultureInfo.InvariantCulture) : v.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>Request written -> result, minus what the CLI says the model API took: the CLI's own overhead.</summary>
    public double CliOverheadMs => ResultMs >= 0 && ApiMs >= 0 ? Math.Max(0, ResultMs - ApiMs) : -1;

    public string OneLine() =>
        $"trace {Backend} {App} {(Retry ? "retry " : "")}{Outcome}: total {Ms(TotalMs)} | sent@{Ms(SentAtMs)} " +
        $"(sel {Ms(SelectionMs)} {SelectionChars} ch, ctx {Ms(ContextMs)}, gate {Ms(GateWaitMs)}{(ColdStart ? $", COLD spawn {Ms(SpawnMs)}" : "")}) | " +
        $"turn {Ms(ResultMs)} (first-out {Ms(FirstOutputMs)}, api {Ms(ApiMs)}, cli-overhead {Ms(CliOverheadMs)}) | " +
        $"parse {Ms(ParseMs)} | dots-stop {Ms(DotsStopMs)} erase {Ms(FinalEraseMs)} type {Ms(TypeMs)} ({TypedChars} ch @ {PaceMs} ms) | " +
        $"cli age {Ms(ProcessAgeMs)} startup-out {Ms(StartupOutputMs)} | prompt {PromptChars} ch {Tokens}";

    public string Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{At:HH:mm:ss}  {Backend}  in {(App.Length > 0 ? App : "?")}{(Retry ? "  (retry)" : "")}  length={Length}  -> {Outcome}");
        void Row(string label, double v, string note = "") =>
            sb.AppendLine($"  {label,-34}{Ms(v),8} ms  {note}");

        sb.AppendLine("  Before sending");
        if (SelectionMs >= 0) Row("copy selection (select-to-ask)", SelectionMs, $"{SelectionChars} chars");
        if (UrlMs >= 0) Row("page address (wait after Enter)", UrlMs, UrlTimedOut ? "not ready in 150 ms: sent without it" : UrlFound ? "found" : "none (not a web page, or private)");
        Row("context (app, title, hint)", ContextMs);
        bool cli = SpawnMs >= 0;
        if (cli) Row("waiting for the CLI to be free", GateWaitMs);
        if (ColdStart) Row(cli ? "CLI startup (cold, on this request)" : "new HTTPS connection (cold)", cli ? SpawnMs : -1,
            cli ? "no warm CLI was ready" : "no warm connection: includes the TLS handshake");
        Row("Enter -> request written", SentAtMs, $"prompt {PromptChars} chars");
        sb.AppendLine(SpawnMs >= 0 ? "  CLI and model" : "  API and model");
        if (cli) Row("CLI process", SpawnMs, $"Process.Start; process was {Ms(ProcessAgeMs)} ms old{(StartupOutputMs >= 0 ? $", printed first line {Ms(StartupOutputMs)} ms after start" : "")}");
        Row(cli ? "request -> first output line" : "request -> first token", FirstOutputMs);
        Row(cli ? "request -> result (whole turn)" : "request -> answer complete", ResultMs, cli ? $"{OutputLines} lines" : Tokens);
        if (ReasoningChars > 0) Row("  model reasoning before answer", -1, $"{ReasoningChars} chars of hidden thinking");
        if (cli)
        {
            Row("  model API time (CLI-reported)", ApiMs, CliReportedMs >= 0 ? $"CLI's own turn total {Ms(CliReportedMs)} ms" : "");
            Row("  CLI overhead (turn - API)", CliOverheadMs, Tokens);
        }
        Row("parse JSON + clean answer", ParseMs);
        sb.AppendLine("  Keystroke injection");
        Row("erase request for dots", EraseRequestMs, "overlaps the model wait");
        Row("stop dots animation", DotsStopMs, "after the answer arrived");
        Row("erase shown text", FinalEraseMs);
        Row("type answer", TypeMs, $"{TypedChars} chars at {PaceMs} ms/key");
        Row("TOTAL Enter -> done", TotalMs, $"answer arrived at {Ms(AnswerAtMs)} ms");
        return sb.ToString();
    }
}

/// <summary>Keeps the last few traces for the Diagnostics window and writes each to the log.</summary>
public static class Diagnostics
{
    private const int Keep = 30;
    private static readonly LinkedList<RequestTrace> Recent = new();
    private static readonly object Gate = new();

    public static event Action? Changed;

    public static void Add(RequestTrace t)
    {
        lock (Gate)
        {
            Recent.AddFirst(t);
            while (Recent.Count > Keep) Recent.RemoveLast();
        }
        Log.Write(t.OneLine());
        Changed?.Invoke();
    }

    public static List<RequestTrace> Snapshot()
    {
        lock (Gate) return [.. Recent];
    }

    public static void Clear()
    {
        lock (Gate) Recent.Clear();
        Changed?.Invoke();
    }

    /// <summary>Medians over the successful requests, newest <paramref name="n"/>.</summary>
    public static string Summary(int n = 10)
    {
        var ok = Snapshot().Where(t => t.Outcome == "ok").Take(n).ToList();
        if (ok.Count == 0) return "No successful requests yet.";
        static double Median(IEnumerable<double> xs)
        {
            var v = xs.Where(x => x >= 0).OrderBy(x => x).ToList();
            return v.Count == 0 ? -1 : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
        }
        string M(Func<RequestTrace, double> f)
        {
            var m = Median(ok.Select(f));
            return m < 0 ? "-" : m.ToString("0", CultureInfo.InvariantCulture);
        }
        return
            $"Median of the last {ok.Count} successful requests (ms):\r\n" +
            $"  Enter -> sent {M(t => t.SentAtMs)} | turn {M(t => t.ResultMs)} = model API {M(t => t.ApiMs)} + CLI overhead {M(t => t.CliOverheadMs)} | " +
            $"parse {M(t => t.ParseMs)} | erase {M(t => t.FinalEraseMs)} + type {M(t => t.TypeMs)} | total {M(t => t.TotalMs)}\r\n" +
            $"  cold starts: {ok.Count(t => t.ColdStart)}/{ok.Count}";
    }
}
