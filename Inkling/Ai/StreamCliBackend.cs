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
    Task<string> AskAsync(string request, string context, CancellationToken ct);
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
    private sealed class Worker(Process process, Channel<string> lines)
    {
        public Process Process { get; } = process;
        public Channel<string> Lines { get; } = lines;
        public int Requests;
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
    protected abstract string BuildInputLine(string request, string context);
    /// <summary>Returns true when <paramref name="line"/> is the final result of the turn.</summary>
    protected abstract bool TryReadResult(JsonElement line, out string? answer, out string? error);

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

    public async Task<string> AskAsync(string request, string context, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var w = EnsureWorker();
            try
            {
                var answer = await RunTurnAsync(w, request, context, ct);
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

    private async Task<string> RunTurnAsync(Worker w, string request, string context, CancellationToken ct)
    {
        // Drop anything left over (e.g. init/hook events) before this turn.
        while (w.Lines.Reader.TryRead(out _)) { }

        await w.Process.StandardInput.WriteLineAsync(BuildInputLine(request, context).AsMemory(), ct);
        await w.Process.StandardInput.FlushAsync(ct);

        while (true)
        {
            string line;
            try { line = await w.Lines.Reader.ReadAsync(ct); }
            catch (ChannelClosedException)
            {
                throw new InvalidOperationException($"{DisplayName} CLI exited unexpectedly (see log).");
            }
            if (line.Length == 0 || line[0] != '{') continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                if (!TryReadResult(doc.RootElement, out var answer, out var error)) continue;
                if (error != null) throw new InvalidOperationException(error);
                return answer ?? "";
            }
        }
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

        var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {psi.FileName}");
        ChildProcessJob.Add(p);
        Log.Write($"{DisplayName}: started CLI pid {p.Id}");

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await p.StandardOutput.ReadLineAsync()) != null) channel.Writer.TryWrite(line);
            }
            catch { /* process killed */ }
            finally { channel.Writer.TryComplete(); }
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
        return new Worker(p, channel);
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

    protected static string UserMessage(string request, string context) =>
        $"{context}\nRequest: {request}";

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

    protected override string BuildInputLine(string request, string context) =>
        JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = UserMessage(request, context) },
        });

    protected override bool TryReadResult(JsonElement line, out string? answer, out string? error)
    {
        answer = error = null;
        if (!line.TryGetProperty("type", out var t) || t.GetString() != "result") return false;
        var text = line.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        bool isError = line.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
        if (isError) error = text ?? "Claude CLI returned an error.";
        else answer = text;
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
    protected override string BuildInputLine(string request, string context) =>
        JsonSerializer.Serialize(new
        {
            @event = "user",
            message = new { role = "user", content = Config.SystemPrompt + "\n\n" + UserMessage(request, context) },
        });

    protected override bool TryReadResult(JsonElement line, out string? answer, out string? error)
    {
        answer = error = null;
        if (!line.TryGetProperty("event", out var ev) || ev.GetString() != "result") return false;
        if (!line.TryGetProperty("result", out var r) || r.ValueKind != JsonValueKind.Object)
        {
            error = "agy returned a malformed result.";
            return true;
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
