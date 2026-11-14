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
///   Idle --rewrite hotkey--> (copy selection) --> Instructing --Enter--> Thinking --answer--> (paste over selection) --> Idle
/// Esc, the hotkey again, cursor movement, a click or a window switch cancel capture.
/// </summary>
internal sealed class CaptureController : IDisposable
{
    private enum State { Idle, Capturing, Instructing, Thinking, Confirming, Typing }

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
    private sealed record LastAnswer(nint Hwnd, string Request, string Context, string Typed, List<string> Rejected, CodeIntent? Code);

    private readonly Func<IAiBackend> _backend;
    private readonly Func<AppConfig> _config;
    private readonly StatusOverlay _overlay;
    private readonly Hooks _hooks;
    private readonly Control _ui;

    private State _state = State.Idle;
    private Hotkey _hotkey;
    private Hotkey? _retryHotkey;
    private Hotkey? _selectionHotkey;
    private Hotkey? _rewriteHotkey;
    /// <summary>The selected text being rewritten (while the instruction is typed).</summary>
    private string? _rewriteSource;
    private nint _targetHwnd;
    private readonly StringBuilder _buffer = new();
    private CancellationTokenSource? _requestCts;
    /// <summary>The browser's page address, read while the request is typed (null for other apps).</summary>
    private Task<string?>? _urlLookup;
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
        _rewriteHotkey = ParseOptional(config().RewriteHotkey);
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
        _rewriteHotkey = ParseOptional(_config().RewriteHotkey);
    }

    private static Hotkey? ParseOptional(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : Hotkey.Parse(text);

    private bool OnKeyDown(KBDLLHOOKSTRUCT info)
    {
        var key = (Keys)info.vkCode;
        bool isHotkey = _hotkey.Matches(info.vkCode);

        // A shortcut held a moment too long repeats its key. In a low-level hook the key's state isn't updated yet,
        // so "already down" means a repeat: swallow it rather than read it as a second press (which cancels).
        if (_state is State.Capturing or State.Instructing or State.Thinking && IsDown((int)info.vkCode)
            && (isHotkey || _selectionHotkey?.Matches(info.vkCode) == true || _rewriteHotkey?.Matches(info.vkCode) == true
                || _retryHotkey?.Matches(info.vkCode) == true))
            return true;

        switch (_state)
        {
            case State.Idle:
                if (Paused || Suspended) { _last = null; return false; }
                bool anyHotkey = isHotkey || _selectionHotkey?.Matches(info.vkCode) == true || _retryHotkey?.Matches(info.vkCode) == true
                                 || _rewriteHotkey?.Matches(info.vkCode) == true;
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
                if (_rewriteHotkey?.Matches(info.vkCode) == true)
                {
                    MaskIfNeeded(_rewriteHotkey);
                    BeginRewrite();
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
                    _requestCts = new CancellationTokenSource(_config().RequestTimeout);
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

            case State.Instructing:
                return OnInstructionKey(info, key);

            case State.Thinking:
                if (key == Keys.Escape || isHotkey || _rewriteHotkey?.Matches(info.vkCode) == true)
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
        else if (_state == State.Instructing) CancelRewrite("mouse click"); // the selection may have changed
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
        _urlLookup = _config().SendOpenItem ? BrowserUrl.StartLookup(_targetHwnd) : null;
    }

    private void BeginRetry(LastAnswer last)
    {
        _targetHwnd = last.Hwnd;
        _dirty = false;
        _userCancelled = false;
        _last = null;
        _state = State.Thinking;
        _requestCts = new CancellationTokenSource(_config().RequestTimeout);
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
        _requestCts = new CancellationTokenSource(_config().RequestTimeout);
        var trace = new RequestTrace();
        _backend().Warm();
        _urlLookup = _config().SendOpenItem ? BrowserUrl.StartLookup(_targetHwnd) : null;
        _ui.BeginInvoke(() => _ = RunSelectionAsync(trace));
    }

    // ---- Rewrite the selection ----------------------------------------------------------------

    /// <summary>Copies the selection (after the hook returns), then waits for the instruction.</summary>
    private void BeginRewrite()
    {
        _targetHwnd = GetForegroundWindow();
        _dirty = false;
        _userCancelled = false;
        _last = null;
        _state = State.Thinking;
        _backend().Warm();
        _urlLookup = _config().SendOpenItem ? BrowserUrl.StartLookup(_targetHwnd) : null;
        _ui.BeginInvoke(() => _ = ReadForRewriteAsync());
    }

    private async Task ReadForRewriteAsync()
    {
        var cfg = _config();
        if (cfg.SelectionSkipApps.Contains(ForegroundContext.ProcessName(_targetHwnd), StringComparer.OrdinalIgnoreCase))
        {
            _state = State.Idle;
            _overlay.ShowMessage(_targetHwnd, "Rewriting isn't supported in terminals", isError: false, autoHideMs: 2500);
            return;
        }
        _overlay.ShowThinking(_targetHwnd, "reading selection");
        string? text = null;
        try { text = await SelectionReader.ReadAsync(_targetHwnd, cfg.MaxSelectionChars); }
        catch (Exception ex) { Log.Write("Selection read failed: " + ex.Message); }
        if (_state != State.Thinking) return;
        if (_userCancelled) { _state = State.Idle; _overlay.HideNow(); return; } // Esc while copying
        if (string.IsNullOrWhiteSpace(text))
        {
            _state = State.Idle;
            _overlay.ShowMessage(_targetHwnd, "Select some text first, then press " + Ui.Wpf.HotkeyBox.Pretty(cfg.RewriteHotkey), isError: false, autoHideMs: 2500);
            return;
        }
        _rewriteSource = text;
        _buffer.Clear();
        _state = State.Instructing;
        ShowInstruction();
    }

    private void ShowInstruction()
    {
        if (_buffer.Length == 0)
        {
            var presets = string.Join(" · ", Prompt.RewritePresets.Select((p, i) => $"{i + 1} {p.Label}"));
            _overlay.ShowInstruction(_targetHwnd, $"Rewrite: type what to change, then Enter   ·   {presets}   ·   Esc = cancel");
        }
        else _overlay.ShowInstruction(_targetHwnd, $"Rewrite: {_buffer}▏   ·   Enter = rewrite   ·   Esc = cancel");
    }

    /// <summary>
    /// Keys while the instruction is typed. They're kept from the app (true = swallowed): the selection is still
    /// selected there, and any key would replace it.
    /// </summary>
    private bool OnInstructionKey(KBDLLHOOKSTRUCT info, Keys key)
    {
        if (GetForegroundWindow() != _targetHwnd) { CancelRewrite("window changed"); return false; }
        if (ModifierKeys.Contains(key)) return false;
        if (key == Keys.Escape) { CancelRewrite(null); return true; }
        if (_rewriteHotkey?.Matches(info.vkCode) == true) { CancelRewrite(null); MaskIfNeeded(_rewriteHotkey); return true; }
        bool altGr = IsDown(VK_RMENU);
        bool chord = !altGr && (IsDown(VK_CONTROL) || IsDown(VK_MENU) || IsDown(VK_LWIN) || IsDown(VK_RWIN));
        if (chord) return true; // shortcuts do nothing here (and mustn't reach the app)

        if (_buffer.Length == 0)
        {
            int preset = key is >= Keys.D1 and <= Keys.D9 ? key - Keys.D1 : key is >= Keys.NumPad1 and <= Keys.NumPad9 ? key - Keys.NumPad1 : -1;
            if (preset >= 0 && preset < Prompt.RewritePresets.Length && !IsDown(VK_SHIFT))
            {
                StartRewrite(Prompt.RewritePresets[preset].Instruction);
                return true;
            }
        }
        if (key == Keys.Return)
        {
            // Enter alone: the first preset (fix grammar).
            StartRewrite(_buffer.Length > 0 ? _buffer.ToString().Trim() : Prompt.RewritePresets[0].Instruction);
            return true;
        }
        if (key == Keys.Back)
        {
            if (_buffer.Length > 0)
            {
                var starts = StringInfo.ParseCombiningCharacters(_buffer.ToString());
                _buffer.Length = starts.Length > 0 ? starts[^1] : 0;
            }
            ShowInstruction();
            return true;
        }
        if (CancelKeys.Contains(key)) return true;
        _buffer.Append(KeyTranslator.Translate(info, _targetHwnd));
        ShowInstruction();
        return true;
    }

    private void CancelRewrite(string? reason)
    {
        _state = State.Idle;
        _rewriteSource = null;
        _buffer.Clear();
        if (reason is null) _overlay.HideNow();
        else _overlay.ShowMessage(_targetHwnd, $"Cancelled ({reason})", isError: false, autoHideMs: 1200);
    }

    private void StartRewrite(string instruction)
    {
        var source = _rewriteSource!;
        _rewriteSource = null;
        _buffer.Clear();
        _state = State.Thinking;
        _requestCts = new CancellationTokenSource(_config().RequestTimeout);
        var trace = new RequestTrace();
        _ui.BeginInvoke(() => _ = RunRewriteAsync(source, instruction, trace));
    }

    private async Task RunRewriteAsync(string source, string instruction, RequestTrace trace)
    {
        var cfg = _config();
        var backend = _backend();
        var cts = _requestCts ??= new CancellationTokenSource(cfg.RequestTimeout);
        trace.Kind = "rewrite";
        trace.Multiline = true;
        trace.MaxTokens = Math.Min(4096, source.Length / 2); // about the selection's length in tokens, with room to grow
        trace.App = ForegroundContext.ProcessName(_targetHwnd);
        trace.Length = Prompt.NormalizeLength(cfg.AnswerLength);
        trace.SelectionChars = source.Length;
        _overlay.ShowThinking(_targetHwnd, backend.DisplayName);

        string? answer = null, failure = null;
        bool failureIsError = true;
        try
        {
            var url = await TakeUrlAsync(trace);
            var context = ForegroundContext.Compose(ForegroundContext.Read(_targetHwnd, cfg, url), cfg);
            var raw = await backend.AskAsync(Prompt.BuildRewrite(context, source, instruction), trace, cts.Token);
            trace.AnswerAtMs = trace.Now;
            answer = OutputCleaner.CleanRewrite(raw, source);
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
            _requestCts?.Dispose();
            _requestCts = null;
        }

        _state = State.Typing;
        try
        {
            if (failure != null)
            {
                trace.Outcome = failure.StartsWith("Error") ? "error" : failure.ToLowerInvariant();
                _overlay.ShowMessage(_targetHwnd, failure, isError: failureIsError, autoHideMs: failureIsError ? 4000 : 1200);
                return;
            }
            // The selection is still selected (no key reached the app): pasting replaces it. If the user clicked or
            // switched windows meanwhile, it may not be: hand the text over instead.
            if (!CanEditTarget)
            {
                trace.Outcome = "clipboard";
                try { Clipboard.SetText(answer!); } catch { /* clipboard busy */ }
                _overlay.ShowMessage(GetForegroundWindow(), "Rewrite copied to clipboard", isError: false, autoHideMs: 2500);
                return;
            }
            await InputSender.WaitForModifiersReleasedAsync();
            var t = trace.Now;
            await InputSender.PasteTextAsync(answer!, _targetHwnd);
            trace.TypeMs = trace.Now - t;
            trace.TypedChars = TextElements(answer!);
            trace.Outcome = "ok";
            _overlay.ShowDone(_targetHwnd);
        }
        finally
        {
            trace.TotalMs = trace.Now;
            Diagnostics.Add(trace);
            _state = State.Idle;
        }
    }

    /// <summary>The page address looked up when the shortcut was pressed; waits for it only briefly.</summary>
    private async Task<string?> TakeUrlAsync(RequestTrace trace)
    {
        if (_urlLookup is not { } lookup) return null;
        _urlLookup = null;
        var u = trace.Now;
        string? url = null;
        if (lookup.IsCompleted || await Task.WhenAny(lookup, Task.Delay(150)) == lookup) url = lookup.Result;
        else trace.UrlTimedOut = true;
        trace.UrlMs = trace.Now - u;
        trace.UrlFound = url != null;
        return url;
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
        _overlay.ShowThinking(_targetHwnd, "reading selection");
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
        var cts = _requestCts ??= new CancellationTokenSource(cfg.RequestTimeout);
        // "i_thnk" for every request: typed, selected or a retry.
        _overlay.ShowThinking(_targetHwnd, backend.DisplayName);
        _shownLength = TextElements(job.OnScreen);
        _selectionShown = job.Selected;
        var process = ForegroundContext.ProcessName(_targetHwnd);
        trace.App = process;
        trace.Length = Prompt.NormalizeLength(cfg.AnswerLength);
        // Some apps (the WinUI Notepad) drop injected keys unless they're spaced out.
        _pace = cfg.SlowApps.Contains(process, StringComparer.OrdinalIgnoreCase) ? cfg.SlowAppDelayMs : cfg.TypingDelayMs;
        trace.PaceMs = _pace;
        _dotFrames = DotFrames(process);

        string context;
        List<string> rejected;
        CodeIntent? code;
        // In a terminal Enter runs what's typed, so answers there stay on one line, code included.
        // So do VS Code and its forks, whose built-in terminal looks the same as the editor from outside.
        bool terminal = cfg.OneLineOnly(process);
        if (job.RetryOf is { } prev)
        {
            context = prev.Context;
            rejected = [.. prev.Rejected, prev.Typed];
            code = prev.Code;
        }
        else
        {
            // The page address was looked up while the request was typed.
            var url = await TakeUrlAsync(trace);
            var t = trace.Now;
            var where = ForegroundContext.Read(_targetHwnd, cfg, url);
            context = ForegroundContext.Compose(where, cfg);
            code = CodeDetector.Detect(job.Request, where.Url, cfg.SendWindowTitle ? where.Title : null, where.File?.Extension);
            trace.ContextMs = trace.Now - t;
            rejected = [];
        }
        trace.Kind = code != null ? "code" : "text";
        trace.Multiline = code != null && !terminal;

        // A snippet answers instantly; otherwise start the request, then swap our text for the dots while it runs.
        var snippet = job.RetryOf == null ? Snippets.Expand(cfg, job.Request) : null;
        if (snippet != null) trace.Backend = "snippet";
        var message = Prompt.Build(cfg, context, job.Request, rejected, code, terminal);
        bool newlines = !cfg.SingleLine || trace.Multiline;

        // Longer prose answers are typed while they stream in, instead of after the model finishes.
        // Not in terminals (Enter), not for code (pasted whole), not for short answers (nothing to gain).
        bool stream = cfg.StreamAnswers && snippet == null && !terminal && !trace.Multiline
                      && Prompt.NormalizeLength(cfg.AnswerLength) != "short";
        var live = new StringBuilder();
        using var liveSignal = new SemaphoreSlim(0);
        bool liveDone = false, liveOff = !stream, liveStarted = false;
        string liveTyped = ""; // what the stream has typed so far, as it appears on screen

        var ask = snippet != null ? Task.FromResult(snippet)
            : stream ? backend.AskAsync(message, trace, cts.Token, piece => { live.Append(piece); liveSignal.Release(); })
            : backend.AskAsync(message, trace, cts.Token);
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

        // Types the stream as it arrives: the first text stops the dots and replaces them.
        async Task PumpAsync()
        {
            while (true)
            {
                await liveSignal.WaitAsync();
                if (liveOff) return;
                bool done = liveDone;
                var text = OutputCleaner.LivePrefix(live.ToString(), cfg.SingleLine, done);
                if (text == null) { liveOff = true; return; } // needs the finished answer to clean it
                var target = InputSender.AsTyped(text, newlines);
                if (target.Length > liveTyped.Length && target.StartsWith(liveTyped, StringComparison.Ordinal))
                {
                    if (!CanEditTarget || cts.IsCancellationRequested) { liveOff = true; return; }
                    if (!liveStarted)
                    {
                        animCts.Cancel();
                        await animation;
                        await InputSender.WaitForModifiersReleasedAsync();
                        if (!CanEditTarget) { liveOff = true; return; }
                        await EraseShownAsync();
                        erased = true;
                        liveStarted = true;
                        trace.Streamed = true;
                    }
                    await InputSender.TypeTextAsync(target[liveTyped.Length..], newlinesAsEnter: newlines, _pace, _targetHwnd);
                    liveTyped = target;
                    _shownLength = TextElements(liveTyped);
                }
                if (done) return;
            }
        }
        var pump = stream ? PumpAsync() : Task.CompletedTask;

        string? answer = null;
        string? failure = null;
        bool failureIsError = true;
        try
        {
            var raw = await ask;
            trace.AnswerAtMs = trace.Now;
            var t = trace.Now;
            answer = snippet ?? OutputCleaner.Clean(raw, cfg.SingleLine && !trace.Multiline);
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
            if (stream)
            {
                liveDone = true;
                if (failure != null) liveOff = true; // stop typing; the failure path below puts the request back
                liveSignal.Release();
                await pump;
            }
            var t = trace.Now;
            animCts.Cancel();
            await animation;
            if (erased && !liveStarted) trace.DotsStopMs = trace.Now - t;
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
                if (liveStarted)
                {
                    // Already partly typed while streaming: take it back, then ask as usual.
                    await InputSender.WaitForModifiersReleasedAsync();
                    await InputSender.BackspaceAsync(TextElements(liveTyped), _pace);
                    liveStarted = false;
                    liveTyped = "";
                    _shownLength = 0;
                }
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
                _overlay.ShowThinking(_targetHwnd, backend.DisplayName); // confirmed: clear the red bar while typing
            }

            await InputSender.WaitForModifiersReleasedAsync();
            var t = trace.Now;
            var typed = InputSender.AsTyped(answer!, newlines);
            if (liveStarted)
            {
                // Most of it is on screen already: make it match the finished, cleaned answer.
                int same = 0;
                while (same < liveTyped.Length && same < typed.Length && liveTyped[same] == typed[same]) same++;
                if (same < liveTyped.Length) await InputSender.BackspaceAsync(TextElements(liveTyped[same..]), _pace);
                if (same < typed.Length) await InputSender.TypeTextAsync(typed[same..], newlinesAsEnter: newlines, _pace, _targetHwnd);
                trace.TypeMs = trace.Now - t;
            }
            else
            {
                await EraseShownAsync();
                trace.FinalEraseMs = trace.Now - t;
                t = trace.Now;
                // Several lines (code) are pasted: editors would re-indent and auto-close brackets as each key arrives.
                // The answer goes onto the clipboard for a moment; what was there is put back afterwards.
                if (trace.Multiline && answer!.Contains('\n')) await InputSender.PasteTextAsync(answer, _targetHwnd);
                else await InputSender.TypeTextAsync(answer!, newlinesAsEnter: newlines, _pace, _targetHwnd);
                trace.TypeMs = trace.Now - t;
            }
            trace.TypedChars = TextElements(typed);
            trace.Outcome = "ok";
            _overlay.ShowDone(_targetHwnd);

            // Retrying would backspace over the answer; a multi-line one may have run things (Enter), so not those.
            if (snippet == null && !typed.Contains('\n') && CanEditTarget && CountIsReliable(process, typed))
                _last = new LastAnswer(_targetHwnd, job.Request, context, typed, rejected, code);
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

    /// <summary>
    /// Office apps AutoCorrect three typed periods into one "…" character, so the animation's backspaces would no
    /// longer match what's on screen and a stray period would be left before the answer. They get middle dots instead.
    /// </summary>
    internal static readonly HashSet<string> AutoCorrectApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "WINWORD", "OUTLOOK", "olk", "POWERPNT", "ONENOTE", "EXCEL",
    };

    /// <summary>The thinking animation's frames for an app: "." ".." "...", or "·" "··" "···" where AutoCorrect would interfere.</summary>
    internal static string[] DotFrames(string process) =>
        AutoCorrectApps.Contains(process) ? ["·", "··", "···"] : [".", "..", "..."];

    private string[] _dotFrames = [".", "..", "..."];

    /// <summary>Sequences Office's AutoCorrect replaces with one character, changing how much text is on screen.</summary>
    private static readonly string[] AutoCorrectTriggers = ["...", "--", "(c)", "(r)", "(tm)", "->", "<-", "=>", "<=", ":)", ":(", ":|"];

    /// <summary>Whether Inkling can trust its count of what it typed, to erase it later (Try again).</summary>
    internal static bool CountIsReliable(string process, string typed) =>
        !AutoCorrectApps.Contains(process) || !AutoCorrectTriggers.Any(t => typed.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>"." → ".." → "..." in the text field, until cancelled.</summary>
    private async Task AnimateDotsAsync(CancellationToken ct)
    {
        string[] frames = _dotFrames;
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
            try { await Task.Delay(300, ct); } // one frame: "." → ".." → "..." takes about a second
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
