using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Inkling.Ai;

/// <summary>
/// Calls an OpenAI-compatible chat API directly: NVIDIA's (NIM) over HTTPS, or a local server on this PC
/// (Ollama, LM Studio) when the backend is "local". No CLI process. What's kept warm is the pooled connection
/// (NIM) or the model in memory (local), from the moment the hotkey is pressed.
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

    /// <summary>A model on this PC: no key, no rate limit, nothing leaves the machine.</summary>
    public bool Local => _config.Backend.Equals("local", StringComparison.OrdinalIgnoreCase);

    public string DisplayName => Local ? $"Local ({_config.LocalModel})" : $"NIM ({ShortModel(_config.NimModel)})";

    private static string ShortModel(string m) => m.Contains('/') ? m[(m.LastIndexOf('/') + 1)..] : m;

    private string BaseUrl => (Local ? _config.LocalBaseUrl : _config.NimBaseUrl).TrimEnd('/');
    private string Model => Local ? _config.LocalModel : _config.NimModel;

    /// <summary>The bearer key: NVIDIA's for NIM; none for a local server.</summary>
    private string? Key => Local ? null : Secrets.NimKey();

    /// <summary>Opens the HTTPS connection in advance, unless it was used recently (a warm-up counts against the rate limit too).</summary>
    public void Warm()
    {
        if (Local) { WarmLocal(); return; }
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

    private static long _lastLocalWarm;
    private static string? _lastLocalModel;

    /// <summary>
    /// A local server loads the model on its first request (seconds for a big one) and unloads it after a few idle
    /// minutes (Ollama: 5). A one-token request while the user types gets it loaded in time.
    /// </summary>
    private void WarmLocal()
    {
        if (_config.LocalModel.Length == 0) return;
        var last = Interlocked.Read(ref _lastLocalWarm);
        // Another model was loaded last: this one isn't in memory, whenever that was.
        if (last != 0 && _lastLocalModel == Model && Stopwatch.GetElapsedTime(last) < TimeSpan.FromMinutes(3)) return;
        Interlocked.Exchange(ref _lastLocalWarm, Stopwatch.GetTimestamp());
        _lastLocalModel = Model;
        _ = Task.Run(async () =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/chat/completions")
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        model = _config.LocalModel,
                        messages = new[] { new { role = "user", content = "hi" } },
                        max_tokens = 1,
                    }), Encoding.UTF8, "application/json"),
                };
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var res = await Http.SendAsync(req, cts.Token);
                Log.Write($"{DisplayName}: model loaded in {sw.ElapsedMilliseconds} ms (HTTP {(int)res.StatusCode})");
            }
            catch (Exception ex) { Log.Write($"{DisplayName}: warm-up failed: {ex.Message}"); }
        });
    }

    /// <summary>Nothing to clear: every request stands alone.</summary>
    public void ClearContext() => Warm();

    public Task<string> AskAsync(string message, RequestTrace trace, CancellationToken ct) => AskAsync(message, trace, ct, null);

    /// <summary>Like <see cref="AskAsync(string, RequestTrace, CancellationToken)"/>, and calls <paramref name="onText"/>
    /// with each piece of the answer as it streams in (on the caller's context).</summary>
    public async Task<string> AskAsync(string message, RequestTrace trace, CancellationToken ct, Action<string>? onText)
    {
        trace.Backend = DisplayName;
        string? key = null;
        if (Local)
        {
            if (_config.LocalModel.Length == 0) throw new InvalidOperationException("No local model picked: Settings → Model");
            trace.ColdStart = false;
        }
        else
        {
            key = Secrets.NimKey() ?? throw new InvalidOperationException("No NIM API key: tray → Set NIM API key…");
            if (!TryReserve(out var waitSeconds))
                throw new InvalidOperationException($"Rate limit ({_config.NimRequestsPerMinute}/min): try again in {waitSeconds} s");
            var last = Interlocked.Read(ref _lastUse);
            trace.ColdStart = last == 0 || Stopwatch.GetElapsedTime(last) > TimeSpan.FromMinutes(2);
            Interlocked.Exchange(ref _lastUse, Stopwatch.GetTimestamp());
        }

        var body = BuildBody(message, trace.Retry, trace.Multiline, trace.MaxTokens);
        trace.PromptChars = message.Length;
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (key != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        trace.SentAtMs = trace.Now;
        var sent = Stopwatch.GetTimestamp();
        HttpResponseMessage res;
        try { res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException ex) when (Local)
        {
            throw new InvalidOperationException($"Can't reach the local model server at {BaseUrl}: is Ollama or LM Studio running? ({ex.Message})");
        }
        using var _res = res;
        if (Local) { Interlocked.Exchange(ref _lastLocalWarm, Stopwatch.GetTimestamp()); _lastLocalModel = Model; }
        if (res.StatusCode == HttpStatusCode.BadRequest && AskNoThinking)
        {
            Log.Write($"{DisplayName}: the server refused reasoning_effort; asking without it");
            _noThinkingRejected = true;
            return await AskAsync(message, trace, ct, onText);
        }
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException(await DescribeErrorAsync(res, ct));

        // Stream the answer; with SingleLine, stop at the first line break (no need to wait for the rest).
        using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var answer = new StringBuilder();
        string? line;
        bool finished = false;
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
                // The model is done once a choice has a finish_reason ("stop", "length"). What may follow is only the
                // usage count, which some servers send seconds later: don't wait for it.
                if (root.TryGetProperty("choices", out var fin) && fin.ValueKind == JsonValueKind.Array && fin.GetArrayLength() > 0
                    && fin[0].TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
                    finished = true;
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("delta", out var delta) &&
                    CountReasoning(delta, trace) && // always true; just counts
                    delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    var piece = c.GetString() ?? "";
                    if (piece.Length > 0 && trace.FirstOutputMs < 0) trace.FirstOutputMs = RequestTrace.Since(sent);
                    if (piece.Length > 0) trace.LastOutputMs = RequestTrace.Since(sent);
                    answer.Append(piece);
                    if (piece.Length > 0) onText?.Invoke(piece);
                }
            }
            catch (JsonException) { }
            finally { trace.ParseMs += RequestTrace.Since(parseStart); }

            if (finished) break;
            if (_config.SingleLine && !trace.Multiline && HasCompleteFirstLine(answer)) break;
        }
        trace.ResultMs = RequestTrace.Since(sent);
        Interlocked.Exchange(ref _lastUse, Stopwatch.GetTimestamp());
        // Everything went into thinking and nothing into the answer: say so, rather than "Empty answer".
        if (answer.ToString().Trim().Length == 0 && trace.ReasoningChars > 0)
            throw new InvalidOperationException(Local
                ? $"{_config.LocalModel} only thought and never answered. Pick a model that doesn't think first (Settings → Model)"
                : "The model only thought and never answered: try again, or pick the other model");
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

    internal string BuildBody(string message, bool retry, bool multiline, int minTokens = 0)
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
        if (multiline) maxTokens = Math.Max(maxTokens, 1536); // code: the whole thing is read, not just the first line
        maxTokens = Math.Max(maxTokens, minTokens);
        var body = new Dictionary<string, object>
        {
            ["model"] = Model,
            ["messages"] = messages,
            ["temperature"] = retry ? 0.7 : 0.2,
            ["max_tokens"] = maxTokens,
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true },
        };
        if (AskNoThinking) body["reasoning_effort"] = "none";
        return JsonSerializer.Serialize(body);
    }

    /// <summary>
    /// Local "thinking" models (qwen3.5, deepseek-r1, ...) otherwise reason for a minute before answering, and can use
    /// up the whole answer length on it. Ollama and LM Studio turn that off with reasoning_effort "none"; a server that
    /// rejects the field gets requests without it from then on.
    /// </summary>
    private bool AskNoThinking => Local && !_noThinkingRejected;
    private static bool _noThinkingRejected;

    private async Task<string> DescribeErrorAsync(HttpResponseMessage res, CancellationToken ct)
    {
        string detail = "";
        try { detail = (await res.Content.ReadAsStringAsync(ct)).ReplaceLineEndings(" "); } catch { }
        if (detail.Length > 200) detail = detail[..200];
        Log.Write($"{DisplayName}: HTTP {(int)res.StatusCode} {detail}");
        if (Local)
            return res.StatusCode == HttpStatusCode.NotFound || detail.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? $"The local server has no model \"{_config.LocalModel}\": pick one in Settings → Model (Ollama: ollama pull <name>)"
                : $"Local model server, HTTP {(int)res.StatusCode}: {detail}";
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
        var backend = new ChatApiBackend(cfg);
        var key = backend.Local ? null : keyOverride ?? Secrets.NimKey();
        if (!backend.Local && key == null) return (false, "No key to test.");
        if (backend.Local && cfg.LocalModel.Length == 0) return (false, "Pick a model first.");
        if (!backend.Local && !backend.TryReserve(out var wait)) return (false, $"Rate limit: try again in {wait} s.");
        try
        {
            var sw = Stopwatch.StartNew();
            var request = new Dictionary<string, object>
            {
                ["model"] = backend.Model,
                ["messages"] = new[] { new { role = "user", content = "Reply with the single word: ok" } },
                ["max_tokens"] = 256,
            };
            if (backend.AskNoThinking) request["reasoning_effort"] = "none";
            var body = JsonSerializer.Serialize(request);
            using var req = new HttpRequestMessage(HttpMethod.Post, backend.BaseUrl + "/chat/completions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (key != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            // A local model may have to load first: give it longer.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(backend.Local ? 90 : 20));
            using var res = await Http.SendAsync(req, cts.Token);
            if (!res.IsSuccessStatusCode) return (false, await backend.DescribeErrorAsync(res, cts.Token));
            Interlocked.Exchange(ref _lastUse, Stopwatch.GetTimestamp());
            return (true, $"Works: {(backend.Local ? cfg.LocalModel : AppConfig.ModelName(cfg.NimModel))} answered in {sw.Elapsed.TotalSeconds:0.0} s.");
        }
        catch (OperationCanceledException)
        {
            return (false, backend.Local ? "No answer within 90 s. The model may be too big for this PC." : "No answer within 20 s. NVIDIA's API may be busy.");
        }
        catch (HttpRequestException) when (backend.Local)
        {
            return (false, $"Nothing answered at {backend.BaseUrl}. Is Ollama or LM Studio running?");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>The models a local server offers (GET /models), for the Settings list. Empty when it can't be reached.</summary>
    public static async Task<List<string>> ListLocalModelsAsync(string baseUrl)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var res = await Http.GetAsync(baseUrl.TrimEnd('/') + "/models", cts.Token);
            if (!res.IsSuccessStatusCode) return [];
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(cts.Token));
            return doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray().Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
                      .OfType<string>().Where(id => !id.Contains("embed", StringComparison.OrdinalIgnoreCase)).OrderBy(id => id).ToList()
                : [];
        }
        catch { return []; }
    }

    public void Dispose() { }
}
