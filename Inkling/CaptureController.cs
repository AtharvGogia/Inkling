using System.Diagnostics;
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
/// Esc, the hotkey again, cursor movement, a click or a window switch cancel capture.
/// </summary>
internal sealed class CaptureController : IDisposable
{
    private enum State { Idle, Capturing, Thinking, Typing }

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

    private readonly Func<IAiBackend> _backend;
    private readonly Func<AppConfig> _config;
    private readonly StatusOverlay _overlay;
    private readonly Hooks _hooks;
    private readonly Control _ui;

    private State _state = State.Idle;
    private Hotkey _hotkey;
    private nint _targetHwnd;
    private readonly StringBuilder _buffer = new();
    private CancellationTokenSource? _requestCts;
    /// <summary>Set when the user typed/clicked while we were waiting: the screen no longer matches the buffer.</summary>
    private bool _dirty;
    private bool _userCancelled;

    public bool Paused { get; set; }

    public CaptureController(Control ui, Func<AppConfig> config, Func<IAiBackend> backend, StatusOverlay overlay)
    {
        _ui = ui;
        _config = config;
        _backend = backend;
        _overlay = overlay;
        _hotkey = Hotkey.Parse(config().Hotkey);
        _hooks = new Hooks { KeyDown = OnKeyDown, MouseDown = OnMouseDown };
    }

    public Hotkey Hotkey => _hotkey;

    public void ReloadHotkey() => _hotkey = Hotkey.Parse(_config().Hotkey);

    private bool OnKeyDown(KBDLLHOOKSTRUCT info)
    {
        var key = (Keys)info.vkCode;
        bool isHotkey = _hotkey.Matches(info.vkCode);

        switch (_state)
        {
            case State.Idle:
                if (isHotkey && !Paused)
                {
                    BeginCapture();
                    MaskIfNeeded();
                    return true;
                }
                return false;

            case State.Capturing:
                if (GetForegroundWindow() != _targetHwnd) { Cancel("window changed"); return false; }
                if (isHotkey) { Cancel(null); MaskIfNeeded(); return true; }
                if (key == Keys.Escape) { Cancel(null); return true; }
                if (key == Keys.Return)
                {
                    if (_buffer.Length == 0) { Cancel(null); return true; }
                    // Created here, not in SubmitAsync, so an Esc that arrives before SubmitAsync runs still cancels.
                    _requestCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config().TimeoutSeconds));
                    // Run after the hook returns: never block or inject from inside the hook callback.
                    _ui.BeginInvoke(SubmitAsync);
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

            default: // Typing: let everything through
                return false;
        }
    }

    private void OnMouseDown()
    {
        if (_state == State.Capturing) Cancel("mouse click");
        else if (_state == State.Thinking) _dirty = true;
    }

    private void MaskIfNeeded()
    {
        if (_hotkey.Alt || _hotkey.Win) _ui.BeginInvoke(InputSender.SendMaskKey);
    }

    private void BeginCapture()
    {
        _targetHwnd = GetForegroundWindow();
        _buffer.Clear();
        _dirty = false;
        _userCancelled = false;
        _state = State.Capturing;
        _overlay.ShowListening(_targetHwnd, 0);
    }

    private void Cancel(string? reason)
    {
        _state = State.Idle;
        _buffer.Clear();
        if (reason is null) _overlay.HideNow();
        else _overlay.ShowMessage(_targetHwnd, $"Cancelled ({reason})", isError: false, autoHideMs: 1200);
    }

    /// <summary>Characters of ours currently in the text field: the request, or the thinking dots.</summary>
    private int _shownLength;
    /// <summary>Pause between injected keys for the current target app.</summary>
    private int _pace;

    private bool CanEditTarget => !_dirty && GetForegroundWindow() == _targetHwnd;

    private async void SubmitAsync()
    {
        var typed = _buffer.ToString();
        var request = typed.Trim();
        var cfg = _config();
        var context = ForegroundContext.Describe(_targetHwnd, cfg);
        var backend = _backend();
        var cts = _requestCts ??= new CancellationTokenSource(TimeSpan.FromSeconds(cfg.TimeoutSeconds));
        _overlay.ShowThinking(_targetHwnd, backend.DisplayName);
        var sw = Stopwatch.StartNew();
        _shownLength = new StringInfo(typed).LengthInTextElements;
        // Some apps (the WinUI Notepad) drop injected keys unless they're spaced out.
        _pace = cfg.SlowApps.Contains(ForegroundContext.ProcessName(_targetHwnd), StringComparer.OrdinalIgnoreCase)
            ? cfg.SlowAppDelayMs : cfg.TypingDelayMs;

        // Start the request first, then swap the request text for the animated dots while it runs.
        var ask = backend.AskAsync(request, context, cts.Token);
        bool requestErased = false;
        using var animCts = new CancellationTokenSource();
        var animation = Task.CompletedTask;
        if (cfg.ThinkingDots)
        {
            await InputSender.WaitForModifiersReleasedAsync();
            if (CanEditTarget && !cts.IsCancellationRequested)
            {
                await InputSender.BackspaceAsync(_shownLength, _pace);
                _shownLength = 0;
                requestErased = true;
                animation = AnimateDotsAsync(animCts.Token);
            }
        }

        string? answer = null;
        string? failure = null;
        bool failureIsError = true;
        try
        {
            answer = OutputCleaner.Clean(await ask, cfg.SingleLine);
            Log.Write($"{backend.DisplayName} answered in {sw.ElapsedMilliseconds} ms ({answer.Length} chars)");
            if (answer.Length == 0) failure = "Empty answer";
        }
        catch (OperationCanceledException)
        {
            failureIsError = !_userCancelled;
            failure = _userCancelled ? "Cancelled" : "Timed out";
        }
        catch (Exception ex)
        {
            Log.Write($"{backend.DisplayName} failed after {sw.ElapsedMilliseconds} ms: {ex.Message}");
            failure = "Error: " + Shorten(ex.Message);
        }
        finally
        {
            animCts.Cancel();
            await animation;
            _requestCts?.Dispose();
            _requestCts = null;
        }

        _state = State.Typing;
        try
        {
            if (failure != null)
            {
                // Put the user's request back in place of the dots so nothing is lost.
                if (requestErased && CanEditTarget)
                {
                    await InputSender.WaitForModifiersReleasedAsync();
                    await InputSender.BackspaceAsync(_shownLength, _pace);
                    await InputSender.TypeTextAsync(typed, newlinesAsEnter: false, _pace, _targetHwnd);
                }
                _overlay.ShowMessage(_targetHwnd, failure, isError: failureIsError, autoHideMs: failureIsError ? 4000 : 1200);
                return;
            }

            // If the user moved on, don't type into the wrong place: hand the answer over via the clipboard.
            if (!CanEditTarget)
            {
                try { Clipboard.SetText(answer!); } catch { /* clipboard busy */ }
                _overlay.ShowMessage(GetForegroundWindow(), "Answer copied to clipboard", isError: false, autoHideMs: 2500);
                return;
            }

            _overlay.HideNow();
            await InputSender.WaitForModifiersReleasedAsync();
            await InputSender.BackspaceAsync(_shownLength, _pace);
            await InputSender.TypeTextAsync(answer!, newlinesAsEnter: !cfg.SingleLine, _pace, _targetHwnd);
        }
        finally
        {
            _state = State.Idle;
            _shownLength = 0;
            _buffer.Clear();
        }
    }

    /// <summary>". " → ". ." → ". . ." in the text field, quickly, until cancelled.</summary>
    private async Task AnimateDotsAsync(CancellationToken ct)
    {
        string[] frames = [".", ". .", ". . ."];
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
