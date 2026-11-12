using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Inkling.Ai;

public interface IAiBackend : IDisposable
{
    string DisplayName { get; }
    /// <summary>Starts the CLI in the background so the first request is fast.</summary>
    void Warm();
    /// <summary>Sends one complete user message (see <see cref="Prompt"/>) and returns the raw answer.</summary>
    Task<string> AskAsync(string message, RequestTrace trace, CancellationToken ct);
    /// <summary>Discards the warm CLI (and any chat history it holds) and starts a fresh one.</summary>
    void ClearContext();
}

/// <summary>
/// Keeps one CLI process alive in "print + stream-json" mode and feeds it one JSON line per request.
/// By default each process answers one request and is then replaced (also after any error or timeout),
/// so requests never see each other's history; the replacement starts in the background right away,
/// so it is warm by the next request.
/// </summary>
public abstract class StreamCliBackend : IAiBackend
{
    private sealed class Worker(Process process, long spawnedAt, double spawnMs)
    {
        public Process Process { get; } = process;
        public Channel<string> Lines { get; } = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        public long SpawnedAt { get; } = spawnedAt;
        public double SpawnMs { get; } = spawnMs;
        /// <summary>Stopwatch timestamp of the first stdout line, 0 until then.</summary>
        public long FirstLineAt;
        public int Requests;
        /// <summary>The last turn returned its answer before the CLI finished the turn; its tail must be drained before reuse.</summary>
        public bool TurnOpen;
        public bool Alive => !Process.HasExited;
    }

    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Worker? _worker;
    private bool _disposed;

    protected AppConfig Config { get; }
    public abstract string DisplayName { get; }

    protected StreamCliBackend(AppConfig config) => Config = config;

    protected abstract string ExecutablePath();
    protected abstract IEnumerable<string> Arguments();
    protected abstract string BuildInputLine(string message);
    /// <summary>
    /// Returns true when <paramref name="line"/> carries the answer (or an error). <paramref name="turnEnd"/> says whether
    /// it is also the end of the CLI's turn; when it isn't, the answer is used right away and the rest of the turn is
    /// skipped. May add CLI-reported stats to the trace.
    /// </summary>
    protected abstract bool TryReadResult(JsonElement line, RequestTrace trace, out string? answer, out string? error, out bool turnEnd);

    public void Warm()
    {
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try { EnsureWorker(); }
            catch (Exception ex) { Log.Write($"{DisplayName}: warm-up failed: {ex.Message}"); }
            finally { _gate.Release(); }
        });
    }

    public void ClearContext()
    {
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync(); // waits for a running request to finish first
            try
            {
                Retire();
                Log.Write($"{DisplayName}: context cleared");
            }
            finally { _gate.Release(); }
        });
    }

    public async Task<string> AskAsync(string message, RequestTrace trace, CancellationToken ct)
    {
        trace.Backend = DisplayName;
        var waitStart = Stopwatch.GetTimestamp();
        await _gate.WaitAsync(ct);
        trace.GateWaitMs = RequestTrace.Since(waitStart);
        try
        {
            trace.ColdStart = _worker is not { Alive: true };
            var w = EnsureWorker();
            trace.SpawnMs = w.SpawnMs;
            trace.ProcessAgeMs = RequestTrace.Since(w.SpawnedAt);
            var first = Interlocked.Read(ref w.FirstLineAt);
            if (first != 0) trace.StartupOutputMs = Stopwatch.GetElapsedTime(w.SpawnedAt, first).TotalMilliseconds;
            try
            {
                var answer = await RunTurnAsync(w, message, trace, ct);
                if (++w.Requests >= Math.Max(1, Config.RequestsPerProcess)) Retire();
                return answer;
            }
            catch
            {
                // Output of a cancelled/failed turn may still arrive later; never reuse this process.
                Retire();
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<string> RunTurnAsync(Worker w, string message, RequestTrace trace, CancellationToken ct)
    {
        if (w.TurnOpen) await DrainTurnAsync(w, ct);
        // Drop anything left over (e.g. init/hook events) before this turn.
        while (w.Lines.Reader.TryRead(out _)) { }

        var input = BuildInputLine(message);
        trace.PromptChars = message.Length;
        trace.SentAtMs = trace.Now;
        var sent = Stopwatch.GetTimestamp();
        await w.Process.StandardInput.WriteLineAsync(input.AsMemory(), ct);
        await w.Process.StandardInput.FlushAsync(ct);

        while (true)
        {
            string line;
            try { line = await w.Lines.Reader.ReadAsync(ct); }
            catch (ChannelClosedException)
            {
                throw new InvalidOperationException($"{DisplayName} CLI exited unexpectedly (see log).");
            }
            if (trace.OutputLines++ == 0) trace.FirstOutputMs = RequestTrace.Since(sent);
            if (line.Length == 0 || line[0] != '{') continue;
            var parseStart = Stopwatch.GetTimestamp();
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                bool done = TryReadResult(doc.RootElement, trace, out var answer, out var error, out var turnEnd);
                trace.ParseMs += RequestTrace.Since(parseStart);
                if (!done) continue;
                trace.ResultMs = RequestTrace.Since(sent);
                w.TurnOpen = !turnEnd;
                if (error != null) throw new InvalidOperationException(error);
                return answer ?? "";
            }
        }
    }

    /// <summary>Reads the end of the previous turn (only needed when a process answers several requests).</summary>
    private async Task DrainTurnAsync(Worker w, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var scratch = new RequestTrace();
        while (true)
        {
            var line = await w.Lines.Reader.ReadAsync(timeout.Token);
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (TryReadResult(doc.RootElement, scratch, out _, out _, out var end) && end) break;
            }
            catch (JsonException) { }
        }
        w.TurnOpen = false;
    }

    private Worker EnsureWorker()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_worker is { Alive: true }) return _worker;
        _worker?.Process.Dispose();
        _worker = Start();
        return _worker;
    }

    /// <summary>Drops the current process (killed in the background) and warms a fresh one. Caller holds the gate.</summary>
    private void Retire()
    {
        var old = _worker;
        _worker = null;
        if (old != null) _ = Task.Run(() => Kill(old.Process));
        if (!_disposed) Warm(); // waits for the gate, i.e. until the caller has returned
    }

    private Worker Start()
    {
        var psi = new ProcessStartInfo(ExecutablePath())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // An empty private folder: no project CLAUDE.md, settings or files for the CLI to pick up.
            WorkingDirectory = WorkDir(),
        };
        foreach (var a in Arguments()) psi.ArgumentList.Add(a);

        var spawnedAt = Stopwatch.GetTimestamp();
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {psi.FileName}");
        var w = new Worker(p, spawnedAt, RequestTrace.Since(spawnedAt));
        ChildProcessJob.Add(p);
        Log.Write($"{DisplayName}: started CLI pid {p.Id} in {w.SpawnMs:0} ms");

        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await p.StandardOutput.ReadLineAsync()) != null)
                {
                    if (Interlocked.Read(ref w.FirstLineAt) == 0) Interlocked.Exchange(ref w.FirstLineAt, Stopwatch.GetTimestamp());
                    w.Lines.Writer.TryWrite(line);
                }
            }
            catch { /* process killed */ }
            finally { w.Lines.Writer.TryComplete(); }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await p.StandardError.ReadLineAsync()) != null)
                    if (line.Contains("error", StringComparison.OrdinalIgnoreCase))
                        Log.Write($"{DisplayName} stderr: {(line.Length > 300 ? line[..300] : line)}");
            }
            catch { }
        });
        return w;
    }

    private static string WorkDir()
    {
        var dir = Path.Combine(AppConfig.Dir, "cli-workdir");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Kill(Process p)
    {
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch { }
        finally { p.Dispose(); }
    }

    /// <summary>Finds an executable on PATH; for npm-style .cmd shims, resolves the real .exe they launch.</summary>
    protected static string ResolveExecutable(string configured, string name)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!File.Exists(configured)) throw new FileNotFoundException($"Configured path not found: {configured}");
            return configured.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ? ExeFromCmdShim(configured) ?? configured : configured;
        }
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var d in dirs)
        {
            var exe = Path.Combine(d, name + ".exe");
            if (File.Exists(exe)) return exe;
        }
        foreach (var d in dirs)
        {
            var cmd = Path.Combine(d, name + ".cmd");
            if (File.Exists(cmd) && ExeFromCmdShim(cmd) is { } real) return real;
        }
        throw new FileNotFoundException($"'{name}' was not found on PATH. Install it or set its path in config.json.");
    }

    private static string? ExeFromCmdShim(string cmdPath)
    {
        // npm shims look like:  "%dp0%\node_modules\...\bin\claude.exe"   %*
        var dir = Path.GetDirectoryName(cmdPath)!;
        foreach (var line in File.ReadLines(cmdPath))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, "\"%dp0%\\\\([^\"]+?\\.exe)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var exe = Path.GetFullPath(Path.Combine(dir, m.Groups[1].Value));
                if (File.Exists(exe)) return exe;
            }
        }
        return null;
    }

    public void Dispose()
    {
        _disposed = true;
        if (_worker != null) Kill(_worker.Process);
        _worker = null;
        GC.SuppressFinalize(this);
    }
}

public sealed class ClaudeBackend(AppConfig config) : StreamCliBackend(config)
{
    public override string DisplayName => $"Claude ({Config.ClaudeModel})";

    protected override string ExecutablePath() => ResolveExecutable(Config.ClaudePath, "claude");

    protected override IEnumerable<string> Arguments()
    {
        var promptFile = Path.Combine(AppConfig.Dir, "system-prompt.txt");
        File.WriteAllText(promptFile, Config.SystemPrompt);
        return
        [
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--model", Config.ClaudeModel,
            "--tools", "",                 // no tools: it can only answer
            "--strict-mcp-config",         // skip the user's MCP servers (faster start, tiny context)
            "--setting-sources", "",       // skip hooks/plugins/CLAUDE.md from user settings
            "--disable-slash-commands",
            "--effort", "low",
            "--no-session-persistence",
            "--system-prompt-file", promptFile,
        ];
    }

    protected override string BuildInputLine(string message) =>
        JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = message },
        });

    protected override bool TryReadResult(JsonElement line, RequestTrace trace, out string? answer, out string? error, out bool turnEnd)
    {
        answer = error = null;
        turnEnd = false;
        if (!line.TryGetProperty("type", out var t)) return false;
        if (t.GetString() == "assistant") return TryReadAssistant(line, trace, out answer);
        if (t.GetString() != "result") return false;
        turnEnd = true;
        if (line.TryGetProperty("duration_ms", out var d) && d.TryGetDouble(out var dms)) trace.CliReportedMs = dms;
        if (line.TryGetProperty("duration_api_ms", out var a) && a.TryGetDouble(out var ams)) trace.ApiMs = ams;
        if (line.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            long Get(string name) => u.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
            trace.Tokens = $"tokens in {Get("input_tokens")} + cache read {Get("cache_read_input_tokens")} / write {Get("cache_creation_input_tokens")}, out {Get("output_tokens")}";
        }
        var text = line.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        bool isError = line.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
        if (isError) error = text ?? "Claude CLI returned an error.";
        else answer = text;
        return true;
    }

    /// <summary>
    /// The model's reply arrives as an "assistant" event; the CLI then makes a second model call (a post-turn
    /// summary) before it prints "result", which costs 1–3 s. With no tools the first text reply is the answer,
    /// so use it straight away. CLI-made error messages (model "&lt;synthetic&gt;") still wait for "result".
    /// </summary>
    private static bool TryReadAssistant(JsonElement line, RequestTrace trace, out string? answer)
    {
        answer = null;
        if (line.TryGetProperty("error", out _)) return false;
        if (!line.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) return false;
        if (m.TryGetProperty("model", out var model) && model.GetString() == "<synthetic>") return false;
        if (!m.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return false;
        var text = string.Concat(content.EnumerateArray()
            .Where(b => b.TryGetProperty("type", out var bt) && bt.GetString() == "text" && b.TryGetProperty("text", out _))
            .Select(b => b.GetProperty("text").GetString()));
        if (text.Trim().Length == 0) return false; // e.g. a thinking block: keep waiting
        if (m.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            long Get(string name) => u.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
            trace.Tokens = $"tokens in {Get("input_tokens")} + cache read {Get("cache_read_input_tokens")} / write {Get("cache_creation_input_tokens")}; answered on the assistant message (CLI's post-turn summary skipped)";
        }
        answer = text;
        return true;
    }
}

/// <summary>Gemini through the Antigravity CLI (agy), which also supports a persistent stream-json mode.</summary>
public sealed class AgyBackend(AppConfig config) : StreamCliBackend(config)
{
    public override string DisplayName => $"Gemini ({Config.AgyModel})";

    protected override string ExecutablePath() => ResolveExecutable(Config.AgyPath, "agy");

    protected override IEnumerable<string> Arguments() =>
    [
        "-p=",                             // agy needs the (empty) prompt attached to the flag
        "--input-format", "stream-json",
        "--output-format", "stream-json",
        "--model", Config.AgyModel,
        "--sandbox",
        "--disable-slash-commands",
    ];

    // agy has no system-prompt flag, so the instructions travel with every message.
    protected override string BuildInputLine(string message) =>
        JsonSerializer.Serialize(new
        {
            @event = "user",
            message = new { role = "user", content = Config.SystemPrompt + "\n\n" + message },
        });

    protected override bool TryReadResult(JsonElement line, RequestTrace trace, out string? answer, out string? error, out bool turnEnd)
    {
        answer = error = null;
        turnEnd = true;
        if (!line.TryGetProperty("event", out var ev)) return false;
        // The model step reports its own duration: the closest thing agy has to "model API time".
        if (ev.GetString() == "step_update" && line.TryGetProperty("step_update", out var su) &&
            su.TryGetProperty("step_type", out var st) && st.GetString() == "agent_response" &&
            su.TryGetProperty("duration_seconds", out var sd) && sd.TryGetDouble(out var secs))
            trace.ApiMs = secs * 1000;
        if (ev.GetString() != "result") return false;
        if (!line.TryGetProperty("result", out var r) || r.ValueKind != JsonValueKind.Object)
        {
            error = "agy returned a malformed result.";
            return true;
        }
        if (r.TryGetProperty("duration_seconds", out var ds) && ds.TryGetDouble(out var total)) trace.CliReportedMs = total * 1000;
        if (r.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            long Get(string name) => u.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
            trace.Tokens = $"tokens in {Get("input_tokens")} (cache read {Get("cache_read_tokens")}), out {Get("output_tokens")}, thinking {Get("thinking_tokens")}";
        }
        var status = r.TryGetProperty("status", out var s) ? s.GetString() : null;
        if (!string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            error = r.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } msg ? msg : $"agy status {status}";
            return true;
        }
        answer = r.TryGetProperty("response", out var resp) ? resp.GetString() : "";
        return true;
    }
}
