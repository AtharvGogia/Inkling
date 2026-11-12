<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/media/header-dark.svg">
    <img src="docs/media/header.svg" height="80" alt="Inkling">
  </picture>
</p>

Inkling puts a short answer right where you're typing, in any app on Windows. Press a shortcut, type what you want ("find files over 100mb in home", "capital of canada", "sum of A1 to A10"), press Enter, and your words turn into the answer.

![Pressing Ctrl+Alt+Space in a terminal, typing "find files over 100mb in home", pressing Enter, and watching it become find ~ -type f -size +100M](docs/media/ask.gif)

It works in terminals, editors, browsers, Notepad, Excel, chat apps: anywhere you can type. Answers are typed in, never run, so you always press Enter yourself.

[![MIT License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![GitHub Release](https://img.shields.io/github/v/release/AtharvGogia/Inkling-for-Windows?include_prereleases&label=Latest%20Release)](https://github.com/AtharvGogia/Inkling-for-Windows/releases)
[![GitHub Downloads](https://img.shields.io/github/downloads/AtharvGogia/Inkling-for-Windows/total?label=Total%20Downloads)](https://github.com/AtharvGogia/Inkling-for-Windows/releases)

Made by **Atharv Gogia**. Open source under the [MIT licence](LICENSE). It's a one-person side project, so if something breaks or you have an idea, I'd really like to hear about it.

## Where it came from

Inkling started out as a folder called "Inline AI" and one simple wish: ask a quick question without leaving the window I'm typing in. No switching to a browser tab, no copying the answer back. Just type it where it's needed and get it there.

The first version answered through the Claude Code and Gemini command-line tools, kept running in the background. It worked, but each answer took around three seconds, and closer to ten when the tool had to start from cold. That's too slow for something meant to feel like autocomplete. So I switched to NVIDIA's free API, tried about 30 of its models, benchmarked the five that answered on questions from a range of subjects (commands, maths, facts, code, translation), and kept the two that were both right and quick. Now an answer often lands in under half a second.

The name was picked from a shortlist with Ghostkey and Spellcast on it: an *inkling* is a small idea, which felt right for short answers. The logo took a few tries, from an ink drop shaped like a fountain-pen nib to the plain wordmark it has now. The orange square is the dot of the i, and the underscore at the end is the text cursor waiting for you to type.

## Download

Get `Inkling-Setup-x.y.z.exe` from the [Releases page](https://github.com/AtharvGogia/Inkling-for-Windows/releases) and run it. It installs for your user only (no admin prompt) and includes everything it needs. Windows 11, 64-bit. It should also run on Windows 10 (1809 or later), but it hasn't been tested there, and its windows are designed for Windows 11.

The installer isn't code-signed, so Windows SmartScreen may say "Windows protected your PC" the first time: choose **More info → Run anyway**.

### Get a free NVIDIA API key

Inkling answers with NVIDIA's free API, so it needs a key. It takes about a minute:

1. Open [build.nvidia.com/settings/api-keys](https://build.nvidia.com/settings/api-keys) and sign in (a free NVIDIA account is enough).
2. Click **Generate API Key** and copy the key. It starts with `nvapi-`.
3. Paste it into Inkling's welcome window, which opens the first time Inkling starts, and click **Save and test**. Later, it's in **Settings → Model**.

The key is kept in Windows Credential Manager (as `Inkling:nim`), never in a file. If you already have `NVIDIA_API_KEY` set in your environment, Inkling uses that.

<img src="docs/media/welcome.png" width="480" alt="The welcome window: how it works, the three steps to get an NVIDIA API key, and start with Windows">

There's also a full **tutorial**, one chapter per feature, including the key and the optional command-line tools: click the tray icon's menu → **Help → Tutorial**, or **Settings → About → Tutorial**.

<img src="docs/media/tutorial-key.png" width="600" alt="The Inkling tutorial, open at the chapter on getting a free NVIDIA key">

Inkling checks GitHub for a new version once a day and offers it with a one-click update. You can also click the tray icon → **Help → Check for updates…**.

## What it does

### Ask

Press **Ctrl+Alt+Space**, type your request, press **Enter**. A small `i_rdy` appears in the top-left corner of the screen while you type (orange). While it thinks, your text becomes a quick `...` and the corner shows `i_thnk` (blue); when the answer is in, `i_fin` (green), and then it goes away. Then the answer is typed in place of your request.

Inkling knows which app you're in and tailors the answer to it: a bash command in a WSL terminal, a PowerShell command in PowerShell, a formula in Excel, code in the right language in your editor (from the file name in the window title), plain words in Notepad.

- **Esc** cancels. While it's thinking, Esc puts your request back.
- Moving the cursor, clicking or switching windows while you type cancels, since Inkling can no longer tell what you typed.
- If you switch away while it's thinking, the answer goes to the clipboard instead of into the wrong window.

### Ask about selected text

Select some text and press **Ctrl+Alt+Enter**. The selection is the request, and the answer replaces it. The corner shows `i_thnk` while it works and `i_fin` when it's done.

![Selecting "translate to french: good morning, my friend" in Notepad, pressing Ctrl+Alt+Enter, and getting "Bonjour, mon ami" in its place](docs/media/select.gif)

The selection is copied with Ctrl+C and your clipboard is put back right after (the copy may still show up in Windows clipboard history). It's off in terminals, where typing doesn't replace a selection and Ctrl+C would interrupt the shell; use Ctrl+Alt+Space there.

### Try again

Not quite right? Press **Ctrl+Alt+R** straight away. The answer is swapped for a different one (with `i_thnk`, then `i_fin`, in the corner), and the model is told which answers you passed on.

![In Notepad, "another word for happy" becomes "joyful"; Ctrl+Alt+R swaps it for "cheerful"](docs/media/retry.gif)

It works until you type, click or switch windows. After that, Ctrl+Alt+R goes to the app as usual.

### Risky commands

If an answer could delete or break things (`rm -rf`, `find … -delete`, `DROP TABLE`, `DELETE` without `WHERE`, `git reset --hard`, `Remove-Item -Recurse`, `format`, shutting down…), it isn't typed straight away. A red bar shows the command and what it would do. **Enter** types it, **Esc** puts your request back.

![Asking a terminal to "delete all .log files in here": the answer find . -name "*.log" -delete is shown in a red bar first, and only typed after Enter](docs/media/risky.gif)

### Snippets

Save phrases of your own in Settings → Snippets. Ask for one and it's typed instantly, without the model: `shrug` gives `¯\_(ツ)_/¯`, `today` gives the date. `{date}`, `{time}` and `{clipboard}` are filled in.

### Answer length

Short (a word or a command, the default), a sentence or two, or a paragraph: in the tray menu or Settings.

## Settings

Click the tray icon. Changes apply straight away.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/media/settings-dark.png">
  <img src="docs/media/settings.png" width="720" alt="Inkling settings: shortcuts, answer length, appearance and start with Windows">
</picture>

- **General:** the three shortcuts (click a box and press the keys; Inkling refuses ones that Windows or every app relies on, like Ctrl+C or Win+L, and warns if another program already uses one), answer length, light or dark, start with Windows.
- **Model:** Nemotron 3.5 Bolt (the default) or Laguna XS 2.1, and your API key.
- **Typing:** the `i_rdy` corner badge or a bar next to the cursor, the `...` animation, typing speed, a slower speed for apps that drop fast typing (Notepad), and one-line answers.
- **Snippets:** your instant replies.
- **Privacy:** the risky-command check, apps where Inkling stays off (password managers by default; add games or remote desktops), and whether window titles are sent.
- **About:** updates, the tutorial, the welcome guide, request timings, the log, and *Copy diagnostics* for bug reports.

## Models and speed

Inkling calls NVIDIA's API directly over HTTPS; nothing runs in the background between requests. The connection is opened the moment you press the shortcut, so it's ready by the time you press Enter, and Inkling stops reading as soon as the first line of the answer is complete.

The two models on offer were picked from NVIDIA's free catalog: about 30 of its chat models were tried, most returned errors or took too long, and the 5 that answered were benchmarked against Claude Haiku with 42 questions across 14 areas (bash, PowerShell, git, Docker, Python, JavaScript, SQL, regex, Excel, facts, maths, units, translation, grammar), each asked as if typed in a matching app:

| Model | Correct | Median time | 90% within |
|---|---|---|---|
| **Nemotron 3.5 Bolt** (default) | 95% | 0.39 s | 1.0 s |
| Laguna XS 2.1 | 95% | 0.69 s | 2.4 s |
| Claude Haiku via Claude Code, for comparison | 93% | 2.0 s | 3.8 s |
| Step 5 Preview | 95% | 6.1 s | 26 s |
| Nemotron 3.5 Lightning | 67% | 7.3 s | 14 s |

Models that think before answering are accurate but far too slow to type in place. The full report is in [`bench/results`](bench/results), and the questions are in [`bench/questions.json`](bench/questions.json).

The free key allows about 40 requests a minute. Inkling stays under that and tells you if you hit it. Settings → Model → *Use a command-line tool instead* can switch to the Claude Code or Gemini (Antigravity) CLI if you have one installed and signed in; the tutorial's chapter 9 walks through setting each one up. For Claude Code that's Node.js 18+ and `npm install -g @anthropic-ai/claude-code`, then running `claude` once to sign in.

## Privacy

Each request sends your request (or selected text), the app's name, the answer length and, if those options are on, the window title and what's open, to NVIDIA's API, which answers it. Inkling has no server of its own and collects nothing.

- **What's open:** in a browser, the page's address, cut down to the site and page (`github.com/AtharvGogia/Inkling-for-Windows/issues`); the part after `?` or `#`, where search terms and tokens live, is never sent. In other apps, the name of the open file (`report.docx`, `notes.ps1`), read from the window title, never its folder. Nothing is sent from private, Incognito or InPrivate windows. Turn it off in Settings → Privacy.

- Your API key is kept in Windows Credential Manager, never in a file.
- The log (`%APPDATA%\Inkling\inkling.log`) holds only timings and errors, never what you typed or the answer.
- Inkling's keyboard hook sees every key press, since that's how it notices the shortcut, but it only keeps what you type after the shortcut, in memory, and never writes it anywhere.
- The only other connection is the daily update check, to GitHub.

## Known limits

- Windows doesn't let a normal app read keys from, or type into, apps running **as administrator**. Run Inkling as administrator too if you need that.
- Inkling tracks what you type from your keystrokes, so autocomplete (shell tab-completion, an IME) can make it erase the wrong amount. Tab cancels for that reason.
- Some apps drop keys typed very fast. Notepad is on the "slower" list by default; add others in Settings → Typing.
- Answers come from a language model and can be wrong. Read commands before you run them.

## Build and run

Needs the .NET 10 SDK, and Inno Setup 6 for the installer (`winget install JRSoftware.InnoSetup`).

```bash
dotnet run --project Inkling
```

Only one copy runs at a time, so quit the installed Inkling first.

Build the installer (a self-contained single-file `Inkling.exe`, and `dist\Inkling-Setup-<version>.exe`):

```bash
pwsh -File build-installer.ps1
```

Developer options of `Inkling.exe`:

| Option | What it does |
|---|---|
| `--ask nim\|claude\|agy "request" ["hint"]` | Asks three times and prints the timing of every step |
| `--bench model1 [model2 …]` | Runs the benchmark and writes a report to `bench/results` |
| `--risk-test` | Checks the risky-command rules against sample commands |
| `--caret-test [seconds]` | Once a second, where the text caret is found in the window in front (sends no input) |
| `--url-test [seconds]` | Once a second, the page address read from the browser in front, as it would be sent |
| `--check-update` | Asks GitHub for the latest release |
| `--render-ui <folder> [light\|dark]` | Saves pictures of the Settings tabs and the welcome window |
| `--render-media <folder>` | Renders the README GIFs (mock scenes drawn frame by frame, encoded with FFmpeg) |
| `--write-icon <file.ico>` | Writes the app icon from the drawing code |
| `--write-logo-svg <folder>` | Writes the README header and logo SVGs, with the letters as outlines |

Any other option prints this list instead of starting Inkling.

## Test it

```bash
dotnet test Inkling.Tests
```

108 unit tests: the risky-command rules, snippets, answer cleaning, shortcut parsing, the prompt, per-app hints, update tags, the corner badge, reading the open file from window titles, trimming page addresses, and the benchmark graders. The keyboard hooks and typing into other apps aren't covered; check those by hand in Notepad and a terminal.

## How it's put together

- `CaptureController.cs`: the shortcut → type → Enter → answer state machine, on low-level keyboard and mouse hooks (`Input/`).
- `Input/InputSender.cs`: types the answer with `SendInput`, key by key, using real keys of your keyboard layout where it can.
- `Ai/ChatApiBackend.cs`: the NVIDIA API (any OpenAI-compatible endpoint), streamed. `Ai/StreamCliBackend.cs`: the optional CLI backends.
- `ForegroundContext.cs` and `Ai/Prompt.cs`: what the model is told about where you're typing.
- `RiskGuard.cs`, `Snippets.cs`, `Updates.cs`, `Secrets.cs` (Credential Manager), `Diagnostics.cs` (timings).
- `Ui/InklingArt.cs`: the logo, drawn from Georgia italic. `docs/media/logo.svg` is the icon as a vector, for anywhere it's needed.
- `Inkling.Tests/`: the unit tests.
- `Ui/Wpf/`: Settings, welcome, tutorial and update windows (WPF with the Windows 11 look). `Ui/StatusOverlay.cs`: the corner badge (`i_rdy`, `i_thnk`, `i_fin`, `i_err`) and the bar.
- `installer/Inkling.iss`: the per-user Inno Setup installer.

## Publishing a release

1. Set `<Version>` in `Inkling/Inkling.csproj` to `x.y.z`.
2. Write the release notes in `docs/releases/x.y.z.md`.
3. Commit and push, then build the installer with `build-installer.ps1`.
4. Publish it, tagged `vx.y.z`:
   `gh release create vx.y.z dist\Inkling-Setup-x.y.z.exe --title "Inkling x.y.z" --notes-file docs/releases/x.y.z.md`

The update window shows the release's description as "what's new". Drafts and pre-releases are never offered as updates.

## Feedback

Inkling is still young, and every report of what tripped someone up makes it better. If something doesn't work, or you'd like it to do something it doesn't, [open an issue](https://github.com/AtharvGogia/Inkling-for-Windows/issues). Settings → About → *Copy diagnostics* puts the useful details on the clipboard (never your key or what you typed). Ideas are just as welcome as bugs.

## Licence

Inkling is open source under the [MIT licence](LICENSE): © 2026 Atharv Gogia.

Thanks for reading, and for trying Inkling. — Atharv
