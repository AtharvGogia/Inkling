# Inkling

Type a question anywhere in Windows and have it replaced in place with a short AI answer.

```
you type:   find files over 100mb in home        ⏎
            . . .
it becomes: find ~ -type f -size +100M
```

Works in any app: Windows Terminal + WSL, PowerShell, Notepad, browsers, editors. The answer is typed in, never run: you still press Enter yourself.

## Install

Run `Inkling-Setup-<version>.exe` (from `dist\`, or build it, see below). It installs for your user only, so no admin rights are needed. It adds a Start-menu entry and can start Inkling when you sign in. Uninstall from *Settings → Apps*. Your settings in `%APPDATA%\Inkling` are kept.

## Use

1. Press **Ctrl+Alt+Space**. A small Inkling light appears in the top-left corner of the screen.
2. Type your request right where you are (e.g. at the bash prompt).
3. Press **Enter**. Your text turns into a quick `.` `. .` `. . .` animation while the model thinks, then the answer is typed in its place.

- **Esc** cancels. Before Enter, your text stays. While thinking, your request is typed back.
- Moving the cursor (arrows, Home/End), clicking, pasting or switching windows cancels capture, since the typed text could no longer be tracked.
- If you switch away or type while it's thinking, the answer goes to the **clipboard** instead of being typed into the wrong place.

### Context

Every request tells the model where the answer is going:
- the app (e.g. *Windows Terminal*, *Notepad*, *Visual Studio Code*)
- the window title, which can show the current folder in a terminal, the file name in an editor, or the page in a browser
- the kind of answer that fits there

So the same words give `s[::-1]` in `strings.py` and `fn rev(s: &str) -> String { … }` in `strings.rs`. Set `SendWindowTitle` to `false` if you don't want window titles sent.

## Tray menu

- **Backend:** Claude or Gemini (see below).
- **Indicator:** small corner light (default) or a full bar with instructions next to the cursor.
- **Clear AI context:** throws away the warm CLI and starts a fresh one.
- **Pause**, **Edit config**, **Reload config**, **Open log**, **Start with Windows**, **Exit**.

## Backends

Uses CLIs you already have and are signed in to. No API keys.

| Backend | CLI | Default model |
|---|---|---|
| Claude | `claude` (Claude Code) | `haiku` |
| Gemini | `agy` (Antigravity CLI) | `gemini-3.8-flash-low` |

The CLI is kept running in the background (`--input-format stream-json`). Each process answers one request and is replaced by a fresh one, started in the background straight away. So requests take about 3–4 s instead of 6–20 s with a cold start, and they never see each other's history.

Claude runs with no tools, no MCP servers and no user settings. CLI processes close when Inkling exits, even if it crashes.

## Config

`%APPDATA%\Inkling\config.json` (tray → *Edit config*, then *Reload config*):

| Key | Default | |
|---|---|---|
| `Hotkey` | `Ctrl+Alt+Space` | Must include Ctrl, Alt or Win |
| `Backend` | `claude` | `claude` or `agy` |
| `ClaudeModel` / `AgyModel` | `haiku` / `gemini-3.8-flash-low` | `agy models` lists the Gemini options |
| `ClaudePath` / `AgyPath` | empty | Empty = find on PATH |
| `Indicator` | `icon` | `icon` (corner light) or `bar` |
| `ThinkingDots` | `true` | Animate `. . .` in the text field while waiting |
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

Check a backend without the hotkey (asks twice, to show cold and warm timing):

```bash
Inkling.exe --ask claude "find files over 100mb in home" "a single bash command for Ubuntu running in WSL2"
```

## Limits

- Windows doesn't let a normal app read keys from, or type into, apps running **as Administrator**. Run Inkling as admin if you need that.
- Text is tracked from your keystrokes, so app autocomplete (e.g. shell tab-completion, IME) can make it erase the wrong amount. Tab cancels capture for that reason.
- Keystrokes are held in memory only while capture is active, and are never written to disk.
