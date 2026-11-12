# Inkling

Type a question anywhere in Windows and have it replaced in place with a short AI answer.

```
you type:   find files over 100mb in home        ⏎
            ...
it becomes: find ~ -type f -size +100M
```

Works in any app: Windows Terminal + WSL, PowerShell, Notepad, browsers, editors. The answer is typed in, never run: you still press Enter yourself.

## Install

Run `Inkling-Setup-<version>.exe` (from `dist\`, or build it, see below). It installs for your user only, so no admin rights are needed. It adds a Start-menu entry and can start Inkling when you sign in. Uninstall from *Settings → Apps*. Your settings in `%APPDATA%\Inkling` are kept.

## Use

1. Press **Ctrl+Alt+Space**. A small Inkling light appears in the top-left corner of the screen.
2. Type your request right where you are (e.g. at the bash prompt).
3. Press **Enter**. Your text turns into a quick `.` `..` `...` animation while the model thinks, then the answer is typed in its place.

- **Esc** cancels. Before Enter, your text stays. While thinking, your request is typed back.
- Moving the cursor (arrows, Home/End), clicking, pasting or switching windows cancels capture, since the typed text could no longer be tracked.
- If you switch away or type while it's thinking, the answer goes to the **clipboard** instead of being typed into the wrong place.
- **Ctrl+Alt+R** right after an answer: erase it and ask again for a different one (the model is told which answers you rejected). It works until you type, click or switch windows; otherwise Ctrl+Alt+R goes to the app as usual.

### Select to ask

Write the request anywhere (or paste it, edit it with the arrow keys, whatever you like), **select it** and press **Ctrl+Alt+Enter**. The selected text is sent as the request and the answer replaces the selection. Retry (Ctrl+Alt+R) works the same way afterwards.

- The selection is copied with Ctrl+C and your clipboard is put back right after. The copy may still show up in Windows clipboard history (Win+V).
- Not available in terminals (`SelectionSkipApps`): typing doesn't replace a terminal selection, and Ctrl+C would interrupt the shell. Use Ctrl+Alt+Space there.
- With nothing selected, you get "Nothing selected" and the text is left alone. In VS Code, the whole-line copy it does when nothing is selected is detected and ignored.

### Answer length

Tray → *Answer length*: **One word / one command** (default), **A sentence or two**, or **A short paragraph**. With `SingleLine` on (the default), longer answers are still kept to one line.

### Context

Every request tells the model where the answer is going:
- the app (e.g. *Windows Terminal*, *Notepad*, *Visual Studio Code*)
- the window title, which can show the current folder in a terminal, the file name in an editor, or the page in a browser
- the kind of answer that fits there
- the answer length you picked

So the same words give `s[::-1]` in `strings.py` and `fn rev(s: &str) -> String { … }` in `strings.rs`. Set `SendWindowTitle` to `false` if you don't want window titles sent.

## Settings

Click the tray icon, or right-click it and pick *Settings…*. Changes apply straight away.

- **General:** the three shortcuts (click a box and press the new one; Backspace turns an optional one off), answer length, light/dark theme, start with Windows. Inkling refuses shortcuts that Windows or every app relies on (Ctrl+C, Win+L…) and warns when another program already uses one.
- **Model:** Nemotron 3.5 Bolt (default) or Laguna XS 2.1, the NVIDIA API key (save, test, remove), and under *Advanced* the Claude Code / Gemini CLIs for when NVIDIA's API is down.
- **Typing:** corner light or instruction bar, the `...` animation, typing speed, slower typing for apps that drop keys, and one-line answers.
- **Privacy & safety:** the risky-command check, apps where Inkling stays off, and whether window titles are sent.
- **About:** the welcome guide, request timings, the log, *Copy diagnostics* (settings and recent log lines for a bug report, never the key or what you typed) and `config.json`.

The first start shows a short **welcome guide**: how it works, the API key (saved and tested), and the main choices.

### Risky commands

If an answer looks destructive, it isn't typed straight away. A red bar shows it and why (deletes files, drops a table, throws away git work, shuts down the PC…). Enter types it, and Esc (or any other key) puts your request back. In the benchmark, one model answered "remove all unused images" with `find … -delete`, which would have deleted every PNG; this check catches that. `Inkling.exe --risk-test` checks the rules against sample commands.

### Apps where Inkling stays off

In apps on this list (KeePass, KeePassXC, 1Password and Bitwarden by default), Inkling's shortcuts do nothing and reach the app as usual. Add games, remote desktops or anything else in *Privacy & safety*.

## Tray menu

*Settings…*, *Model* (Bolt or Laguna), *Answer length*, *Pause Inkling*, *Help* (welcome guide, request timings, log) and *Exit*.

## Backends

| Backend | How | Default model | Warm request → answer |
|---|---|---|---|
| **NVIDIA NIM** (default) | HTTPS API, your NIM key | `nvidia/nemotron-3.5-bolt-e4b` | ≈ 0.4–0.7 s |
| Claude | `claude` CLI (Claude Code) | `haiku` | ≈ 1.0 s |
| Gemini | `agy` CLI (Antigravity) | `gemini-3.8-flash-low` | several seconds |

### NVIDIA NIM (no CLI)

Requests go straight to `https://integrate.api.nvidia.com/v1/chat/completions` (OpenAI-compatible), streamed. No process is kept running: the only thing warmed is the HTTPS connection, opened when you press the hotkey, so the TLS handshake happens while you type. With `SingleLine`, Inkling stops reading as soon as the first line is complete.

- **Key:** *Settings → Model* (or the welcome guide) stores it in Windows Credential Manager as `Inkling:nim`. It is never put in `config.json` or the log. Without a stored key, the `NVIDIA_API_KEY` environment variable is used.
- **Model:** *Settings → Model* or the tray offers the two models that did best in the benchmark (see below). Any other NVIDIA model id can be set in `config.json`; NVIDIA's catalog lists more models than it actually serves, and those return 404.

  Benchmark (42 questions in 14 domains, `bench/questions.json`, run with `--bench`):

  | Model | Correct | Median | p90 |
  |---|---|---|---|
  | **Nemotron 3.5 Bolt** | 95% | 0.39 s | 1.0 s |
  | Laguna XS 2.1 | 95% | 0.69 s | 2.4 s |
  | Claude haiku (CLI, for comparison) | 93% | 2.0 s | 3.8 s |
  | Step 5 Preview | 95% | 6.1 s | 26 s |
  | Nemotron 3.5 Lightning | 67% | 7.3 s | 14 s |

  Reasoning models think before answering, so they're too slow to type in place. Most other models in NVIDIA's catalog returned 404.
- **Rate limit:** the free tier allows about 40 requests a minute per key, shared with any other tool using the same key. Inkling keeps its own count (`NimRequestsPerMinute`, 38). Past it, you get "try again in N s" right away and your text is put back. Warm-ups are skipped when the connection was used in the last 90 s, so normal use is one request per Enter.
- **Other servers:** `NimBaseUrl` can point at any OpenAI-compatible API, e.g. a local model in Ollama: `http://localhost:11434/v1`.
- Requests go to NVIDIA's cloud, with the same context as the other backends: app name, window title (if `SendWindowTitle`) and your request or selection.

### Style without fine-tuning

NIM's hosted API can't be fine-tuned. That needs NVIDIA's NeMo Customizer on your own GPUs, which isn't worth it here. It isn't needed either: the system prompt and context are only a few hundred tokens. The API backend also sends a few **examples** (`Examples` in config: hint, request, ideal answer) as earlier chat turns. Small models copy the style of examples far more reliably than they follow rules, so edit those to change how answers look.

### CLI backends

These use CLIs you already have and are signed in to, with no API key. The CLI is kept running in the background (`--input-format stream-json`). Each process answers one request and is replaced by a fresh one, started in the background straight away, so requests never see each other's history.

For Claude, the answer is taken from the CLI's `assistant` message as soon as it arrives. The CLI then makes a second model call (a post-turn summary) before it prints its final `result`, which used to add 1.3–3 s to every request. A warm request now takes about **1 s** from Enter to answer (it was about 3.9 s), plus the typing. agy only starts up when it receives its first message, so warming it in advance doesn't help much, and each request sends it ~15k tokens of its own prompt. Expect several seconds with agy.

Claude runs with no tools, no MCP servers and no user settings. CLI processes close when Inkling exits, even if it crashes.

## Diagnostics

Tray → *Diagnostics (timings)* shows the last 30 requests, with medians at the top:

- **Before sending:** copying the selection (select to ask), reading the app context, waiting for the CLI, and a cold start (a new CLI, or a new HTTPS connection for NIM)
- **CLI/API and model:** for CLIs, process start and age, request → first output line, request → answer and the model time the CLI reports. For NIM, request → first token and request → answer complete. Tokens used for both.
- **Parsing:** JSON parsing of the CLI output and cleaning the answer
- **Keystroke injection:** erasing the request for the dots, stopping the dots, erasing, and typing the answer (characters × ms per key)

The window holds no prompts or answers, only lengths and times. Each request also writes a `trace` line to the log. `Inkling.exe --ask nim|claude|agy "…"` prints the same breakdown for three requests.

Measured on one PC, warm: Enter → request sent < 1 ms, request → answer ≈ 0.4–0.7 s with NIM (Nemotron 3.5 Bolt) or ≈ 1.0 s with Claude haiku, parse < 1 ms, typing ≈ 0.5 s for 15 characters in Notepad (30 ms/key) or ≈ 0.1 s elsewhere (5 ms/key).

## Config

`%APPDATA%\Inkling\config.json`. Most of it is in Settings; the rest (prompt, examples, per-app hints, paths) is edited by hand (*Settings → About → Open config.json*) and picked up the next time Settings opens. Saves are atomic, and an unreadable file is kept aside as `config.invalid-<time>.json` instead of being overwritten.

| Key | Default | |
|---|---|---|
| `Hotkey` | `Ctrl+Alt+Space` | Must include Ctrl, Alt or Win |
| `RetryHotkey` | `Ctrl+Alt+R` | Ask again right after an answer. Empty = off |
| `SelectionHotkey` | `Ctrl+Alt+Enter` | Select to ask. Empty = off |
| `MaxSelectionChars` / `SelectionSkipApps` | `4000` / terminals | Longest selection sent; apps where select-to-ask is refused |
| `Theme` | `system` | `system`, `light` or `dark` for Inkling's windows |
| `RiskGuard` | `true` | Confirm destructive-looking answers before typing them |
| `DisabledApps` | password managers | Process names where Inkling's shortcuts do nothing |
| `FirstRunDone` | `false` | Set once the welcome guide has been closed |
| `AnswerLength` | `short` | `short`, `sentence` or `paragraph` |
| `Backend` | `nim` | `nim`, `claude` or `agy` |
| `NimModel` / `NimBaseUrl` | `nvidia/nemotron-3.5-bolt-e4b` / NVIDIA's API | Model (Settings offers Bolt and Laguna; any id works here) and OpenAI-compatible endpoint |
| `NimRequestsPerMinute` | `38` | Inkling's own cap, under the key's ~40/min |
| `Examples` | 5 pairs | Few-shot examples (hint, request, answer) for the API backend |
| `ClaudeModel` / `AgyModel` | `haiku` / `gemini-3.8-flash-low` | `agy models` lists the Gemini options |
| `ClaudePath` / `AgyPath` | empty | Empty = find on PATH |
| `Indicator` | `icon` | `icon` (corner light) or `bar` |
| `ThinkingDots` | `true` | Animate `...` in the text field while waiting |
| `SendWindowTitle` | `true` | Include the window title in the context |
| `SingleLine` | `true` | Keep only the first line. A newline would run a command in a terminal |
| `TypingDelayMs` | `5` | Pause between typed keys |
| `SlowApps` / `SlowAppDelayMs` | `["Notepad"]` / `30` | Apps that drop fast typing get a slower pace |
| `RequestsPerProcess` | `1` | Raise to reuse one CLI process for several requests (they then share history) |
| `TimeoutSeconds` | `45` | |
| `SystemPrompt`, `DefaultHint`, `Contexts` | | Prompting and per-app answer hints |

Log (timings and errors only, never what you typed): `%APPDATA%\Inkling\inkling.log`.

## Build

Requires the .NET 10 SDK, plus Inno Setup 6 for the installer (`winget install JRSoftware.InnoSetup`).

```bash
dotnet build Inkling -c Debug
```

Installer: publishes a self-contained single-file `Inkling.exe` and writes `dist\Inkling-Setup-<version>.exe`:

```bash
pwsh -File build-installer.ps1
```

Check a backend without the hotkey (asks three times, to show cold and warm timing, with the full breakdown):

```bash
Inkling.exe --ask nim "find files over 100mb in home" "a single bash command for Ubuntu running in WSL2"
```

Benchmark models for accuracy and speed. This runs the 42 questions in `bench/questions.json` (14 domains, each asked as if typed in a matching app) through the real request path and writes a Markdown report to `bench/results/`. Model names are NIM model ids, or `claude` / `agy`. NIM requests are paced to stay under the key's rate limit.

```bash
Inkling.exe --bench nvidia/nemotron-3.5-bolt-e4b poolside/laguna-xs-2.1 claude
```

## Limits

- Windows doesn't let a normal app read keys from, or type into, apps running **as Administrator**. Run Inkling as admin if you need that.
- Text is tracked from your keystrokes, so app autocomplete (e.g. shell tab-completion, IME) can make it erase the wrong amount. Tab cancels capture for that reason.
- Keystrokes are held in memory only while capture is active, and are never written to disk.
