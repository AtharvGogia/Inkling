using System.Globalization;
using System.Text;
using Inkling.Ai;
using Inkling.Input;
using Inkling.Ui;
using static Inkling.Native;

namespace Inkling;

/// <summary>
/// The hotkey → type → Enter → replace state machine. Lives on the UI thread.
///   Idle --hotkey--> Capturing --Enter--> Thinking --answer--> (erase + type) --> Idle
///   Idle --retry hotkey (right after an answer)--> Thinking (answer erased and asked again)
/// Esc, the hotkey again, cursor movement, a click or a window switch cancel capture.
/// </summary>
internal sealed class CaptureController : IDisposable
{
    private enum State { Idle, Capturing, Thinking, Confirming, Typing }

    private static readonly HashSet<Keys> CancelKeys =
    [
        Keys.Left, Keys.Right, Keys.Up, Keys.Down, Keys.Home, Keys.End, Keys.PageUp, Keys.PageDown,
        Keys.Delete, Keys.Insert, Keys.Tab,
    ];

    private static readonly HashSet<Keys> ModifierKeys =
    [
        Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey, Keys.ControlKey, Keys.LControlKey, Keys.RControlKey,
        Keys.Menu, Keys.LMenu, Keys.RMenu, Keys.LWin, Keys.RWin, Keys.Capital, Keys.NumLock, Keys.Scroll,
    ];

    /// <summary>One request to the model, and what is on screen in its place while it runs.</summary>
    private sealed class Job
    {
        public required string Request { get; init; }
        /// <summary>Text of ours in the field when the job starts (the typed request, or the answer being retried); restored on failure.</summary>
        public required string OnScreen { get; init; }
        public required RequestTrace Trace { get; init; }
        /// <summary>OnScreen is still selected (select-to-ask): one Backspace removes it, and typing replaces it.</summary>
        public bool Selected { get; init; }
        /// <summary>Set for a retry: the context is reused from the first attempt.</summary>
        public LastAnswer? RetryOf { get; init; }
    }

    /// <summary>The last answer typed, kept until the user does anything else, so it can be retried.</summary>
    private sealed record LastAnswer(nint Hwnd, string Request, string Context, string Typed, List<string> Rejected);

    private readonly Func<IAiBackend> _backend;
    private readonly Func<AppConfig> _config;
    private readonly StatusOverlay _overlay;
    private readonly Hooks _hooks;
    private readonly Control _ui;

    private State _state = State.Idle;
    private Hotkey _hotkey;
    private Hotkey? _retryHotkey;
    private Hotkey? _selectionHotkey;
    private nint _targetHwnd;
    private readonly StringBuilder _buffer = new();
    private CancellationTokenSource? _requestCts;
    private Job? _pending;
    private LastAnswer? _last;
    /// <summary>Set when the user typed/clicked while we were waiting: the screen no longer matches the buffer.</summary>
    private bool _dirty;
    private bool _userCancelled;

    public bool Paused { get; set; }
    /// <summary>Set while Settings records a hotkey, so pressing it there doesn't trigger Inkling.</summary>
    public bool Suspended { get; set; }
    /// <summary>Answer of a risky-command confirmation: true = type it.</summary>
    private TaskCompletionSource<bool>? _confirm;

    /// <summary>The foreground app is on the user's "never activate here" list.</summary>
    private bool DisabledHere()
    {
        var list = _config().DisabledApps;
        return list.Count > 0 && list.Contains(ForegroundContext.ProcessName(GetForegroundWindow()), StringComparer.OrdinalIgnoreCase);
    }

    public CaptureController(Control ui, Func<AppConfig> config, Func<IAiBackend> backend, StatusOverlay overlay)
    {
        _ui = ui;
        _config = config;
        _backend = backend;
        _overlay = overlay;
        _hotkey = Hotkey.Parse(config().Hotkey);
        _retryHotkey = ParseOptional(config().RetryHotkey);
        _selectionHotkey = ParseOptional(config().SelectionHotkey);
        _hooks = new Hooks { KeyDown = OnKeyDown, MouseDown = OnMouseDown };
    }

    public Hotkey Hotkey => _hotkey;
    public Hotkey? RetryHotkey => _retryHotkey;
    public Hotkey? SelectionHotkey => _selectionHotkey;

    public void ReloadHotkey()
    {
        _hotkey = Hotkey.Parse(_config().Hotkey);
        _retryHotkey = ParseOptional(_config().RetryHotkey);
        _selectionHotkey = ParseOptional(_config().SelectionHotkey);
    }

    private static Hotkey? ParseOptional(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : Hotkey.Parse(text);

    private bool OnKeyDown(KBDLLHOOKSTRUCT info)
    {
        var key = (Keys)info.vkCode;
        bool isHotkey = _hotkey.Matches(info.vkCode);

        switch (_state)
        {
            case State.Idle:
                if (Paused || Suspended) { _last = null; return false; }
                bool anyHotkey = isHotkey || _selectionHotkey?.Matches(info.vkCode) == true || _retryHotkey?.Matches(info.vkCode) == true;
                if (anyHotkey && DisabledHere()) return false; // pass the keys through untouched
                if (isHotkey)
                {
                    MaskIfNeeded();
                    BeginCapture();
                    return true;
                }
                if (_selectionHotkey?.Matches(info.vkCode) == true)
                {
                    MaskIfNeeded(_selectionHotkey);
                    BeginSelection();
                    return true;
                }
                if (_retryHotkey?.Matches(info.vkCode) == true && _last is { } last && GetForegroundWindow() == last.Hwnd)
                {
                    MaskIfNeeded(_retryHotkey);
                    BeginRetry(last);
                    return true;
                }
                // Anything else the user does means the answer is no longer "just typed".
                if (!ModifierKeys.Contains(key)) _last = null;
                return false;

            case State.Capturing:
                if (GetForegroundWindow() != _targetHwnd) { Cancel("window changed"); return false; }
                if (isHotkey) { Cancel(null); MaskIfNeeded(); return true; }
                if (key == Keys.Escape) { Cancel(null); return true; }
                if (key == Keys.Return)
                {
                    if (_buffer.Length == 0) { Cancel(null); return true; }
                    var typed = _buffer.ToString();
                    var trace = new RequestTrace();
                    // Created here, not in RunAsync, so an Esc that arrives before RunAsync runs still cancels.
                    _requestCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config().TimeoutSeconds));
                    _pending = new Job { Request = typed.Trim(), OnScreen = typed, Trace = trace };
                    // Run after the hook returns: never block or inject from inside the hook callback.
                    _ui.BeginInvoke(RunPending);
                    _state = State.Thinking;
                    return true; // swallow Enter so the shell doesn't run the request text
                }
                if (ModifierKeys.Contains(key)) return false;
                if (key == Keys.Back)
                {
                    if (IsDown(VK_CONTROL)) { Cancel("Ctrl+Backspace"); return false; }
                    if (_buffer.Length > 0)
                    {
                        // Remove the last text element (handles surrogate pairs / combined chars).
                        var s = _buffer.ToString();
                        var starts = StringInfo.ParseCombiningCharacters(s);
                        _buffer.Length = starts.Length > 0 ? starts[^1] : 0;
                    }
                    return false;
                }
                if (CancelKeys.Contains(key)) { Cancel("cursor moved"); return false; }
                bool altGr = IsDown(VK_RMENU);
                if (!altGr && (IsDown(VK_CONTROL) || IsDown(VK_MENU) || IsDown(VK_LWIN) || IsDown(VK_RWIN)))
                {
                    Cancel("shortcut pressed"); // e.g. Ctrl+V: we can't know what was pasted
                    return false;
                }
                _buffer.Append(KeyTranslator.Translate(info, _targetHwnd));
                _overlay.ShowListening(_targetHwnd, _buffer.Length);
                return false;

            case State.Thinking:
                if (key == Keys.Escape || isHotkey)
                {
                    _userCancelled = true;
                    _requestCts?.Cancel();
                    if (isHotkey) MaskIfNeeded();
                    return true;
                }
                if (!ModifierKeys.Contains(key)) _dirty = true;
                return false;

            case State.Confirming:
                if (ModifierKeys.Contains(key)) return false;
                if (key == Keys.Return) { _confirm?.TrySetResult(true); return true; }
                // Esc, or anything else: don't type it. Another key goes to the app, so the field changed.
                if (key != Keys.Escape) _dirty = true;
                _confirm?.TrySetResult(false);
                return key == Keys.Escape;

            default: // Typing: let everything through
                return false;
        }
    }

    private void OnMouseDown()
    {
        if (_state == State.Confirming) _confirm?.TrySetResult(false);
        else if (_state == State.Capturing) Cancel("mouse click");
        else if (_state == State.Thinking) _dirty = true;
        else if (_state == State.Idle) _last = null;
    }

    private void MaskIfNeeded(Hotkey? hk = null)
    {
        hk ??= _hotkey;
        if (hk.Alt || hk.Win) _ui.BeginInvoke(InputSender.SendMaskKey);
    }

    private void BeginCapture()
    {
        _targetHwnd = GetForegroundWindow();
        _buffer.Clear();
        _dirty = false;
        _userCancelled = false;
        _last = null;
        _state = State.Capturing;
        _overlay.ShowListening(_targetHwnd, 0);
        _backend().Warm(); // API backend: opens the HTTPS connection while you type
    }

    private void BeginRetry(LastAnswer last)
    {
        _targetHwnd = last.Hwnd;
        _dirty = false;
        _userCancelled = false;
        _last = null;
        _state = State.Thinking;
        _requestCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config().TimeoutSeconds));
        _pending = new Job { Request = last.Request, OnScreen = last.Typed, Trace = new RequestTrace { Retry = true }, RetryOf = last };
        _ui.BeginInvoke(RunPending);
    }

    /// <summary>Select-to-ask: the selected text is the request. Copying it happens after the hook returns.</summary>
    private void BeginSelection()
    {
        _targetHwnd = GetForegroundWindow();
        _dirty = false;
        _userCancelled = false;
        _last = null;
        _state = State.Thinking;
        _requestCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config().TimeoutSeconds));
        var trace = new RequestTrace();
        _backend().Warm();
        _ui.BeginInvoke(() => _ = RunSelectionAsync(trace));
    }

    private async Task RunSelectionAsync(RequestTrace trace)
    {
        var cfg = _config();
        void Stop(string? message)
        {
            _state = State.Idle;
            _requestCts?.Dispose();
            _requestCts = null;
            if (message is null) _overlay.HideNow();
            else _overlay.ShowMessage(_targetHwnd, message, isError: false, autoHideMs: 2500);
        }

        var process = ForegroundContext.ProcessName(_targetHwnd);
        if (cfg.SelectionSkipApps.Contains(process, StringComparer.OrdinalIgnoreCase))
        {
            Stop($"Select-to-ask isn't supported in terminals: use {_hotkey} and type the request");
            return;
        }
        // Select-to-ask shows no indicator: the selection turning into the answer is feedback enough.
        string? text = null;
        try
        {
            text = await SelectionReader.ReadAsync(_targetHwnd, cfg.MaxSelectionChars);
        }
        catch (Exception ex) { Log.Write("Selection read failed: " + ex.Message); }
        trace.SelectionMs = trace.Now;
        trace.SelectionChars = text?.Length ?? 0;

        if (_requestCts == null || _requestCts.IsCancellationRequested) { Stop(null); return; }
        if (string.IsNullOrWhiteSpace(text)) { Stop("Nothing selected"); return; }
        if (!CanEditTarget) { Stop("Cancelled (window changed)"); return; }
        await RunAsync(new Job { Request = text.Trim(), OnScreen = text, Selected = true, Trace = trace });
    }

    private void Cancel(string? reason)
    {
        _state = State.Idle;
        _buffer.Clear();
        if (reason is null) _overlay.HideNow();
        else _overlay.ShowMessage(_targetHwnd, $"Cancelled ({reason})", isError: false, autoHideMs: 1200);
    }

    /// <summary>Characters of ours currently in the text field: the request, the dots or the old answer.</summary>
    private int _shownLength;
    /// <summary>Pause between injected keys for the current target app.</summary>
    private int _pace;
    /// <summary>What is shown is still a live selection: a single Backspace removes all of it.</summary>
    private bool _selectionShown;

    private async Task EraseShownAsync()
    {
        if (_selectionShown) await InputSender.BackspaceAsync(1, _pace);
        else await InputSender.BackspaceAsync(_shownLength, _pace);
        _selectionShown = false;
        _shownLength = 0;
    }

    private bool CanEditTarget => !_dirty && GetForegroundWindow() == _targetHwnd;

    private static int TextElements(string s) => new StringInfo(s).LengthInTextElements;

    private void RunPending()
    {
        var job = _pending;
        _pending = null;
        if (job != null) _ = RunAsync(job);
    }

    private async Task RunAsync(Job job)
    {
        var cfg = _config();
        var trace = job.Trace;
        var backend = _backend();
        var cts = _requestCts ??= new CancellationTokenSource(TimeSpan.FromSeconds(cfg.TimeoutSeconds));
        // Only a typed request gets the "i_..." badge; select-to-ask and retry stay quiet.
        if (!job.Selected && job.RetryOf == null) _overlay.ShowThinking(_targetHwnd, backend.DisplayName);
        else _overlay.HideNow();
        _shownLength = TextElements(job.OnScreen);
        _selectionShown = job.Selected;
        var process = ForegroundContext.ProcessName(_targetHwnd);
        trace.App = process;
        trace.Length = Prompt.NormalizeLength(cfg.AnswerLength);
        // Some apps (the WinUI Notepad) drop injected keys unless they're spaced out.
        _pace = cfg.SlowApps.Contains(process, StringComparer.OrdinalIgnoreCase) ? cfg.SlowAppDelayMs : cfg.TypingDelayMs;
        trace.PaceMs = _pace;

        string context;
        List<string> rejected;
        if (job.RetryOf is { } prev)
        {
            context = prev.Context;
            rejected = [.. prev.Rejected, prev.Typed];
        }
        else
        {
            var t = trace.Now;
            context = ForegroundContext.Describe(_targetHwnd, cfg);
            trace.ContextMs = trace.Now - t;
            rejected = [];
        }

        // A snippet answers instantly; otherwise start the request, then swap our text for the dots while it runs.
        var snippet = job.RetryOf == null ? Snippets.Expand(cfg, job.Request) : null;
        if (snippet != null) trace.Backend = "snippet";
        var message = Prompt.Build(cfg, context, job.Request, rejected);
        var ask = snippet != null ? Task.FromResult(snippet) : backend.AskAsync(message, trace, cts.Token);
        bool erased = false;
        using var animCts = new CancellationTokenSource();
        var animation = Task.CompletedTask;
        if (cfg.ThinkingDots && snippet == null)
        {
            await InputSender.WaitForModifiersReleasedAsync();
            if (CanEditTarget && !cts.IsCancellationRequested)
            {
                var t = trace.Now;
                await EraseShownAsync();
                trace.EraseRequestMs = trace.Now - t;
                erased = true;
                animation = AnimateDotsAsync(animCts.Token);
            }
        }

        string? answer = null;
        string? failure = null;
        bool failureIsError = true;
        try
        {
            var raw = await ask;
            trace.AnswerAtMs = trace.Now;
            var t = trace.Now;
            answer = snippet ?? OutputCleaner.Clean(raw, cfg.SingleLine);
            trace.ParseMs += trace.Now - t;
            if (answer.Length == 0) failure = "Empty answer";
        }
        catch (OperationCanceledException)
        {
            failureIsError = !_userCancelled;
            failure = _userCancelled ? "Cancelled" : "Timed out";
        }
        catch (Exception ex)
        {
            Log.Write($"{backend.DisplayName} failed after {trace.Now:0} ms: {ex.Message}");
            failure = "Error: " + Shorten(ex.Message);
        }
        finally
        {
            var t = trace.Now;
            animCts.Cancel();
            await animation;
            if (erased) trace.DotsStopMs = trace.Now - t;
            _requestCts?.Dispose();
            _requestCts = null;
        }

        _state = State.Typing;
        try
        {
            if (failure != null)
            {
                trace.Outcome = failure.StartsWith("Error") ? "error" : failure.ToLowerInvariant();
                // Put back what was there (the request, or the answer being retried) so nothing is lost.
                if (erased && CanEditTarget)
                {
                    await InputSender.WaitForModifiersReleasedAsync();
                    await EraseShownAsync();
                    // A selection can span lines (select-to-ask never runs in terminals, so Enter is a newline there).
                    await InputSender.TypeTextAsync(job.OnScreen, newlinesAsEnter: job.Selected, _pace, _targetHwnd);
                }
                if (job.RetryOf != null && CanEditTarget) _last = job.RetryOf; // the old answer is back: still retryable
                _overlay.ShowMessage(_targetHwnd, failure, isError: failureIsError, autoHideMs: failureIsError ? 4000 : 1200);
                return;
            }

            // If the user moved on, don't type into the wrong place: hand the answer over via the clipboard.
            if (!CanEditTarget)
            {
                trace.Outcome = "clipboard";
                try { Clipboard.SetText(answer!); } catch { /* clipboard busy */ }
                _overlay.ShowMessage(GetForegroundWindow(), "Answer copied to clipboard", isError: false, autoHideMs: 2500);
                return;
            }

            // Destructive-looking commands are shown first and only typed after Enter.
            if (cfg.RiskGuard && snippet == null && RiskGuard.Check(answer!) is { } why)
            {
                _confirm = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _state = State.Confirming;
                _overlay.ShowConfirm(_targetHwnd, $"Careful, this {why}:  {Shorten(answer!)}   ·   Enter = type it   ·   Esc = cancel");
                var go = await _confirm.Task;
                _confirm = null;
                _state = State.Typing;
                if (!go || !CanEditTarget)
                {
                    trace.Outcome = "risky-cancelled";
                    if (CanEditTarget)
                    {
                        await InputSender.WaitForModifiersReleasedAsync();
                        await EraseShownAsync();
                        await InputSender.TypeTextAsync(job.OnScreen, newlinesAsEnter: job.Selected, _pace, _targetHwnd);
                    }
                    _overlay.ShowMessage(_targetHwnd, "Not typed", isError: false, autoHideMs: 1200);
                    return;
                }
            }

            _overlay.HideNow();
            await InputSender.WaitForModifiersReleasedAsync();
            var t = trace.Now;
            await EraseShownAsync();
            trace.FinalEraseMs = trace.Now - t;
            bool newlines = !cfg.SingleLine;
            t = trace.Now;
            await InputSender.TypeTextAsync(answer!, newlinesAsEnter: newlines, _pace, _targetHwnd);
            trace.TypeMs = trace.Now - t;
            var typed = InputSender.AsTyped(answer!, newlines);
            trace.TypedChars = TextElements(typed);
            trace.Outcome = "ok";

            // Retrying would backspace over the answer; a multi-line one may have run things (Enter), so not those.
            if (snippet == null && !typed.Contains('\n') && CanEditTarget)
                _last = new LastAnswer(_targetHwnd, job.Request, context, typed, rejected);
        }
        finally
        {
            trace.TotalMs = trace.Now;
            Diagnostics.Add(trace);
            _state = State.Idle;
            _shownLength = 0;
            _selectionShown = false;
            _buffer.Clear();
        }
    }

    /// <summary>"." → ".." → "..." in the text field, quickly, until cancelled.</summary>
    private async Task AnimateDotsAsync(CancellationToken ct)
    {
        string[] frames = [".", "..", "..."];
        string current = "";
        for (int i = 0; !ct.IsCancellationRequested; i++)
        {
            if (!CanEditTarget) return; // user moved on: stop touching the field
            var next = frames[i % frames.Length];
            if (next.StartsWith(current, StringComparison.Ordinal))
            {
                await InputSender.TypeTextAsync(next[current.Length..], newlinesAsEnter: false, _pace, _targetHwnd);
            }
            else
            {
                await InputSender.BackspaceAsync(current.Length, _pace);
                await InputSender.TypeTextAsync(next, newlinesAsEnter: false, _pace, _targetHwnd);
            }
            current = next;
            _shownLength = current.Length;
            try { await Task.Delay(130, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static string Shorten(string s)
    {
        s = s.ReplaceLineEndings(" ");
        return s.Length > 120 ? s[..117] + "..." : s;
    }

    public void Dispose()
    {
        _requestCts?.Cancel();
        _hooks.Dispose();
    }
}
