using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Inkling.Ai;

/// <summary>
/// Calls an OpenAI-compatible chat API (NVIDIA NIM by default) directly over HTTPS: no CLI process.
/// The only thing kept warm is the pooled TLS connection, opened when the hotkey is pressed.
/// </summary>
public sealed class ChatApiBackend : IAiBackend
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        EnableMultipleHttp2Connections = true,
    })
    {
        DefaultRequestVersion = HttpVersion.Version20,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        Timeout = Timeout.InfiniteTimeSpan, // each request carries its own cancellation
    };

    /// <summary>Requests sent in the last minute (all backends share one key, so this is static).</summary>
    private static readonly Queue<long> Sent = new();
    private static readonly object SentGate = new();
    /// <summary>Stopwatch timestamp of the last request on the connection, 0 = never.</summary>
    private static long _lastUse;

    private readonly AppConfig _config;

    public ChatApiBackend(AppConfig config) => _config = config;

    public string DisplayName => $"NIM ({ShortModel(_config.NimModel)})";

    private static string ShortModel(string m) => m.Contains('/') ? m[(m.LastIndexOf('/') + 1)..] : m;

    private string BaseUrl => _config.NimBaseUrl.TrimEnd('/');

    /// <summary>Opens the HTTPS connection in advance, unless it was used recently (a warm-up counts against the rate limit too).</summary>
    public void Warm()
    {
        var last = Interlocked.Read(ref _lastUse);
        if (last != 0 && Stopwatch.GetElapsedTime(last) < TimeSpan.FromSeconds(90)) return;
        if (Secrets.NimKey() is not { } key) return;
        if (!TryReserve(out _)) return;
        Interlocked.Exchange(ref _lastUse, Stopwatch.GetTimestamp());
        _ = Task.Run(async () =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/models");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                Log.Write($"{DisplayName}: connection warmed in {sw.ElapsedMilliseconds} ms (HTTP {(int)res.StatusCode})");
            }
            catch (Exception ex) { Log.Write($"{DisplayName}: warm-up failed: {ex.Message}"); }
        });
    }

    /// <summary>Nothing to clear: every request stands alone.</summary>
    public void ClearContext() => Warm();

    public async Task<string> AskAsync(string message, RequestTrace trace, CancellationToken ct)
    {
        trace.Backend = DisplayName;
        var key = Secrets.NimKey() ?? throw new InvalidOperationException("No NIM API key: tray → Set NIM API key…");
        if (!TryReserve(out var waitSeconds))
            throw new InvalidOperationException($"Rate limit ({_config.NimRequestsPerMinute}/min): try again in {waitSeconds} s");

        var last = Interlocked.Read(ref _lastUse);
        trace.ColdStart = last == 0 || Stopwatch.GetElapsedTime(last) > TimeSpan.FromMinutes(2);
        Interlocked.Exchange(ref _lastUse, Stopwatch.GetTimestamp());

        var body = BuildBody(message, trace.Retry);
        trace.PromptChars = message.Length;
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        trace.SentAtMs = trace.Now;
        var sent = Stopwatch.GetTimestamp();
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(await DescribeErrorAsync(res, ct));

        // Stream the answer; with SingleLine, stop at the first line break (no need to wait for the rest).
        using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var answer = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line.AsSpan(5).Trim();
            if (data.SequenceEqual("[DONE]")) break;
            trace.OutputLines++;
            var parseStart = Stopwatch.GetTimestamp();
            try
            {
                using var doc = JsonDocument.Parse(data.ToString());
                var root = doc.RootElement;
                if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    long Get(string name) => u.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
                    trace.Tokens = $"tokens in {Get("prompt_tokens")}, out {Get("completion_tokens")}";
                }
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("delta", out var delta) &&
                    CountReasoning(delta, trace) && // always true; just counts
                    delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    var piece = c.GetString() ?? "";
                    if (piece.Length > 0 && trace.FirstOutputMs < 0) trace.FirstOutputMs = RequestTrace.Since(sent);
                    answer.Append(piece);
                }
            }
            catch (JsonException) { }
            finally { trace.ParseMs += RequestTrace.Since(parseStart); }

            if (_config.SingleLine && HasCompleteFirstLine(answer)) break;
        }
        trace.ResultMs = RequestTrace.Since(sent);
        Interlocked.Exchange(ref _lastUse, Stopwatch.GetTimestamp());
        return answer.ToString();
    }

    /// <summary>Reasoning models stream their thinking separately; count it so the trace shows where the time went.</summary>
    private static bool CountReasoning(JsonElement delta, RequestTrace trace)
    {
        foreach (var name in (string[])["reasoning_content", "reasoning"])
            if (delta.TryGetProperty(name, out var r) && r.ValueKind == JsonValueKind.String)
            {
                if (trace.ReasoningChars == 0) trace.ReasoningStartMs = trace.Now;
                trace.ReasoningChars += r.GetString()?.Length ?? 0;
            }
        return true;
    }

    /// <summary>A line break after some visible text, outside a ``` fence and a &lt;think&gt; block.</summary>
    private static bool HasCompleteFirstLine(StringBuilder sb)
    {
        var s = sb.ToString();
        if (s.Contains("<think>") && !s.Contains("</think>")) return false;
        var start = s.Contains("</think>") ? s.IndexOf("</think>", StringComparison.Ordinal) + 8 : 0;
        var rest = s[start..].TrimStart();
        if (rest.StartsWith("```")) return rest.IndexOf("```", 3, StringComparison.Ordinal) > 0;
        var nl = rest.IndexOf('\n');
        return nl > 0 && rest[..nl].Trim().Length > 0;
    }

    private string BuildBody(string message, bool retry)
    {
        var messages = new List<object> { new { role = "system", content = _config.SystemPrompt } };
        foreach (var ex in _config.Examples)
        {
            messages.Add(new { role = "user", content = $"Answer type wanted here: {ex.Hint}.\nRequest: {ex.Request}" });
            messages.Add(new { role = "assistant", content = ex.Answer });
        }
        messages.Add(new { role = "user", content = message });
        // Generous on purpose: reasoning models spend tokens thinking before the answer, and for the others
        // reading stops at the end of the first line anyway (SingleLine), so a high cap costs nothing.
        int maxTokens = Prompt.NormalizeLength(_config.AnswerLength) switch { "sentence" => 1024, "paragraph" => 1536, _ => 768 };
        return JsonSerializer.Serialize(new
        {
            model = _config.NimModel,
            messages,
            temperature = retry ? 0.7 : 0.2,
            max_tokens = maxTokens,
            stream = true,
            stream_options = new { include_usage = true },
        });
    }

    private async Task<string> DescribeErrorAsync(HttpResponseMessage res, CancellationToken ct)
    {
        string detail = "";
        try { detail = (await res.Content.ReadAsStringAsync(ct)).ReplaceLineEndings(" "); } catch { }
        if (detail.Length > 200) detail = detail[..200];
        Log.Write($"{DisplayName}: HTTP {(int)res.StatusCode} {detail}");
        return res.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "NIM key rejected: tray → Set NIM API key…",
            HttpStatusCode.TooManyRequests =>
                $"NIM rate limit hit{(res.Headers.RetryAfter?.Delta is { } d ? $": try again in {Math.Ceiling(d.TotalSeconds)} s" : "")} (the key is shared with other NIM tools)",
            HttpStatusCode.NotFound => $"Model {_config.NimModel} not available: pick one in the tray",
            _ => $"NIM HTTP {(int)res.StatusCode}: {detail}",
        };
    }

    /// <summary>Client-side sliding-window limiter, so a burst fails fast instead of waiting on a 429.</summary>
    private bool TryReserve(out int waitSeconds)
    {
        waitSeconds = 0;
        var limit = Math.Max(1, _config.NimRequestsPerMinute);
        var now = Stopwatch.GetTimestamp();
        lock (SentGate)
        {
            while (Sent.Count > 0 && Stopwatch.GetElapsedTime(Sent.Peek(), now) > TimeSpan.FromMinutes(1)) Sent.Dequeue();
            if (Sent.Count >= limit)
            {
                waitSeconds = (int)Math.Ceiling(60 - Stopwatch.GetElapsedTime(Sent.Peek(), now).TotalSeconds);
                return false;
            }
            Sent.Enqueue(now);
            return true;
        }
    }

    /// <summary>Checks a key (or the saved one) and the chosen model with one tiny request. For Settings and the welcome window.</summary>
    public static async Task<(bool Ok, string Message)> TestAsync(AppConfig cfg, string? keyOverride)
    {
        var key = keyOverride ?? Secrets.NimKey();
        if (key == null) return (false, "No key to test.");
        var backend = new ChatApiBackend(cfg);
        if (!backend.TryReserve(out var wait)) return (false, $"Rate limit: try again in {wait} s.");
        try
        {
            var sw = Stopwatch.StartNew();
            var body = JsonSerializer.Serialize(new
            {
                model = cfg.NimModel,
                messages = new[] { new { role = "user", content = "Reply with the single word: ok" } },
                max_tokens = 256,
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, backend.BaseUrl + "/chat/completions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var res = await Http.SendAsync(req, cts.Token);
            if (!res.IsSuccessStatusCode) return (false, await backend.DescribeErrorAsync(res, cts.Token));
            Interlocked.Exchange(ref _lastUse, Stopwatch.GetTimestamp());
            return (true, $"Works: {AppConfig.ModelName(cfg.NimModel)} answered in {sw.Elapsed.TotalSeconds:0.0} s.");
        }
        catch (OperationCanceledException) { return (false, "No answer within 20 s. NVIDIA's API may be busy."); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public void Dispose() { }
}
