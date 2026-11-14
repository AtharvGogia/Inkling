using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WpfButton = System.Windows.Controls.Button;
using WpfClipboard = System.Windows.Clipboard;
using WpfListBox = System.Windows.Controls.ListBox;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBlock = System.Windows.Controls.TextBlock;

namespace Inkling.Ui.Wpf;

/// <summary>
/// The tutorial: everything Inkling does, one chapter at a time, including getting an NVIDIA key and setting up
/// the optional command-line tools. Opened from the tray (Help), Settings → About and the welcome window.
/// </summary>
internal sealed class TutorialWindow : Window
{
    // ---- Content ---------------------------------------------------------------------------------------
    private abstract record Block;
    private sealed record Para(string Text, string? LinkText = null, string? Url = null) : Block;
    private sealed record Steps(params string[] Items) : Block;
    private sealed record Code(string Text) : Block;
    private sealed record Sub(string Text) : Block;
    private sealed record Note(string Text) : Block;
    private sealed record Chapter(string Title, Block[] Blocks);

    private static Chapter[] Chapters(AppConfig c)
    {
        string ask = HotkeyBox.Pretty(c.Hotkey);
        string sel = c.SelectionHotkey.Length > 0 ? HotkeyBox.Pretty(c.SelectionHotkey) : "(off: turn it on in Settings → General)";
        string retry = c.RetryHotkey.Length > 0 ? HotkeyBox.Pretty(c.RetryHotkey) : "(off: turn it on in Settings → General)";
        string rewrite = c.RewriteHotkey.Length > 0 ? HotkeyBox.Pretty(c.RewriteHotkey) : "(off: turn it on in Settings → General)";
        return
        [
            new("Welcome", [
                new Para("Inkling puts a short answer right where you're typing, in any app: a terminal, an editor, a browser, Notepad, Excel, a chat box."),
                new Steps($"Press {ask}.", "Type what you want, in plain words.", "Press Enter. Your words turn into the answer."),
                new Code("find files over 100mb in home   →   find ~ -type f -size +100M"),
                new Para("Answers are typed in, never run: in a terminal you still press Enter yourself. Esc cancels at any point."),
                new Para("The chapters on the left go through everything, starting with the one thing Inkling needs: a free NVIDIA API key."),
            ]),
            new("1. Get a free NVIDIA key", [
                new Para("Inkling answers with NVIDIA's free API. It needs a key, which takes about a minute to get."),
                new Steps(
                    "Open build.nvidia.com/settings/api-keys and sign in. A free NVIDIA account is enough.",
                    "Click Generate API Key, and copy the key. It starts with nvapi-.",
                    "In Inkling, open Settings (click the tray icon) → Model, paste the key under NVIDIA API key and click Save.",
                    "Click Test. You should see “Works: Nemotron 3.5 Bolt answered in …”."),
                new Para("Open the key page: ", "build.nvidia.com/settings/api-keys", AppInfo.NvidiaKeysUrl),
                new Note("The key is kept in Windows Credential Manager (as “Inkling:nim”), never in a file or the log. If you already have NVIDIA_API_KEY set in your environment, Inkling uses that and you can skip this."),
                new Para("The free key allows about 40 requests a minute. Inkling stays under that and tells you if you hit it, for example if other tools use the same key."),
            ]),
            new("2. Ask", [
                new Para($"Press {ask} wherever you can type. A small i_rdy appears in the top-left corner of the screen: Inkling is listening."),
                new Para("Type your request and press Enter. While Inkling thinks, your text turns into “...” and the corner shows i_thnk. Then the answer is typed in its place and the corner shows i_fin for a moment."),
                new Sub("It knows where you are"),
                new Para("The answer fits the app: a bash command in a WSL terminal, a PowerShell command in PowerShell, a formula in Excel, code in the language of the file open in your editor, plain words in Notepad."),
                new Sub("Things that cancel"),
                new Steps("Esc. While Inkling is thinking, Esc puts your request back.",
                          "Moving the cursor (arrow keys, Home, End), clicking, pasting or switching windows while you type, because Inkling can no longer tell what's in the field.",
                          "If you switch away while it thinks, the answer goes to the clipboard instead of into the wrong window."),
            ]),
            new("3. Ask about selected text", [
                new Para($"Select some text and press {sel}. The selection is the request, and the answer replaces it."),
                new Code("translate to french: good morning, my friend   →   Bonjour, mon ami"),
                new Para("The corner shows i_thnk while it works and i_fin when it's done."),
                new Note("Inkling copies the selection with Ctrl+C and puts your clipboard back straight after; the copy may still show in Windows clipboard history (Win+V). It's off in terminals, where Ctrl+C would interrupt the shell."),
            ]),
            new("4. Rewrite selected text", [
                new Para($"Select some text, press {rewrite}, type what to change and press Enter. The selection is replaced with the rewritten text."),
                new Code("make it more formal\nturn it into bullet points\ntranslate to german\nadd type hints"),
                new Para("While you type the instruction, your keys go to Inkling, not the app, so the selection stays safe. Common ones need no typing: press 1 to fix the grammar (or just Enter), 2 for shorter, 3 for more formal, 4 for friendlier, 5 for bullet points."),
                new Note("The rewritten text is pasted in, so line breaks and indentation come through as written, and your clipboard is put back afterwards. Esc cancels. It's off in terminals."),
            ]),
            new("5. Try again", [
                new Para($"Not quite right? Press {retry} straight away. The answer is swapped for a different one, and the model is told which answers you passed on."),
                new Para("It works until you type, click or switch windows. After that, the shortcut goes to the app as usual."),
            ]),
            new("6. Answer length", [
                new Para("Short (a word or a command) is the default. For more, pick A sentence or two, or A paragraph, in the tray menu or Settings → General."),
                new Para("With Keep answers on one line (Settings → Typing), longer answers stay on one line, so a terminal never gets an Enter it didn't ask for."),
                new Para("Longer answers start appearing as soon as the model starts writing them, instead of all at once at the end. You can turn that off in Settings → Typing."),
                new Sub("Code"),
                new Para("Ask for code (\"create and reverse a string in rust\", \"python function that checks for primes\") and you get the code itself, on as many lines as it needs, whatever the length setting. On a code site such as the Rust Playground, or in a code file in your editor, you don't even need to name the language: \"create and reverse a string\" is enough."),
                new Para("Code is pasted rather than typed, so the editor doesn't re-indent it. Your clipboard is put back right after, and the code stays out of clipboard history. In a terminal, code requests still get a single command, and so do VS Code and its forks, whose built-in terminal Inkling can't tell apart from the editor."),
            ]),
            new("7. Snippets", [
                new Para("Snippets are your own instant replies, with no model involved. Ask for a snippet's name and its text is typed straight away."),
                new Steps("Open Settings → Snippets and click Add snippet.",
                          "Under You type, enter the name (for example: sig).",
                          "Under You get, enter the text to type."),
                new Para("{date}, {time}, {datetime} and {clipboard} are filled in, so “today” → {date} gives today's date."),
            ]),
            new("8. Risky commands", [
                new Para("If an answer could delete or break things (rm -rf, find … -delete, DROP TABLE, DELETE without WHERE, git reset --hard, Remove-Item -Recurse, format, shutting down…), it isn't typed straight away."),
                new Para("A red bar shows the command and what it would do. Enter types it; Esc, or any other key, puts your request back."),
                new Note("You can turn this off in Settings → Privacy, but it's worth keeping: answers come from a language model and can be wrong."),
            ]),
            new("9. Privacy and apps to skip", [
                new Para("Each request sends your request (or selected text), the app's name and, if those options are on, the window title and what's open: the page's address in a browser (just the site and page, never the part after ? or #), or the file's name in other apps (never its folder). Nothing from private browser windows. It all goes to NVIDIA's API; Inkling has no server of its own and collects nothing."),
                new Para("Both options are in Settings → Privacy, under Sent with each request."),
                new Para("The keyboard hook sees every key press (that's how it spots the shortcut), but only what you type after the shortcut is kept, in memory, and never written anywhere. The log holds timings and errors, not text."),
                new Sub("Turn Inkling off in some apps"),
                new Para("In Settings → Privacy, under Turned off in, add apps where Inkling's shortcuts should do nothing: password managers (KeePass, 1Password and Bitwarden are there already), games, remote desktops."),
            ]),
            new("10. Run a model on this PC", [
                new Para("Instead of NVIDIA's API, Inkling can use a model running on your own PC. Nothing leaves your computer, it works offline, and there's no rate limit. Small models answer fast but make more mistakes; bigger ones need a good graphics card."),
                new Sub("With Ollama"),
                new Steps("Install Ollama from ollama.com.",
                          "In a terminal, download a model with the command below (or any other model from ollama.com).",
                          "In Inkling: Settings → Model → Or run a model on this PC. Click Refresh, pick the model, click Test, then choose Use a local model."),
                new Code("ollama pull llama3.2"),
                new Sub("With LM Studio"),
                new Steps("In LM Studio, download and load a model, then start its local server (Developer tab).",
                          "In Inkling, set the server to http://localhost:1234/v1, click Refresh and pick the model."),
                new Note("The first answer after a while can take a few seconds: the model is loaded into memory when you press the shortcut. Switch back any time from the tray's Model menu."),
            ]),
            new("11. Use a command-line AI instead", [
                new Para("If NVIDIA's API is down, or you'd rather use a tool you already pay for, Inkling can ask the Claude Code CLI or Google's Antigravity CLI. Each keeps a copy running in the background so it's ready, but answers take 1 to 5 seconds instead of under one."),
                new Sub("Claude Code"),
                new Steps("Install Node.js 18 or later (nodejs.org), then in a terminal run the command below.",
                          "Run claude once and sign in with your Claude account. Close it when it's done.",
                          "In Inkling: Settings → Model → Use a command-line tool instead → Claude Code CLI."),
                new Code("npm install -g @anthropic-ai/claude-code"),
                new Para("Inkling uses the haiku model by default (ClaudeModel in config.json). More on installing: ", "Claude Code docs", "https://docs.anthropic.com/en/docs/claude-code/setup"),
                new Sub("Antigravity (Gemini)"),
                new Steps("Install Google's Antigravity CLI (agy) and sign in, following Google's instructions.",
                          "Check it works: run agy models in a terminal. It lists the models you can use.",
                          "In Inkling: Settings → Model → Use a command-line tool instead → Gemini via the Antigravity CLI."),
                new Code("agy models"),
                new Para("Inkling uses gemini-3.8-flash-low by default (AgyModel in config.json)."),
                new Note("Inkling finds both tools on your PATH. If it can't, set ClaudePath or AgyPath in config.json (Settings → About → Open config.json) to the full path of claude.exe or agy.exe. Restart the CLI from Settings → Model if one gets stuck."),
            ]),
            new("12. If something doesn't work", [
                new Sub("Nothing happens when I press the shortcut"),
                new Para("Check the tray icon isn't paused, and that the app isn't on the Turned off in list. Windows doesn't let a normal app read keys from apps running as administrator; run Inkling as administrator too if you need that. Another program may also use the same shortcut: change it in Settings → General."),
                new Sub("“NIM key rejected” or “Model not available”"),
                new Para("Make a new key (chapter 1) and click Test in Settings → Model. If a model stops answering, pick the other one."),
                new Sub("“Rate limit: try again in N s”"),
                new Para("The free key allows about 40 requests a minute, shared with anything else using it. Wait a few seconds."),
                new Sub("Letters come out mixed up or missing"),
                new Para("Some apps can't keep up with fast typing. Add the app under Slower in these apps (Settings → Typing), or raise the typing speed slider."),
                new Sub("The wrong amount was erased"),
                new Para("Autocomplete (shell tab-completion, an input method) changes the text without Inkling seeing it. Press the shortcut again on a clean line."),
                new Sub("Still stuck?"),
                new Para("Settings → About → Copy diagnostics puts the useful details on the clipboard (never your key or what you typed). Then ", "open an issue on GitHub", AppInfo.SourceUrl + "/issues"),
            ]),
        ];
    }

    // ---- Window ----------------------------------------------------------------------------------------
    private readonly Chapter[] _chapters;
    private readonly WpfListBox _list = new() { Width = 230, BorderThickness = new Thickness(0) };
    private readonly StackPanel _page = new() { Margin = new Thickness(4, 0, 16, 16) };
    private readonly ScrollViewer _scroll;
    private readonly WpfButton _prev = new() { Content = "Previous", MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) };
    private readonly WpfButton _next = new() { Content = "Next", MinWidth = 96 };

    public TutorialWindow(AppConfig cfg)
    {
        Title = "Inkling tutorial";
        Width = 900;
        Height = 640;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = SettingsWindow.AppIcon();
        _chapters = Chapters(cfg);

        foreach (var c in _chapters) _list.Items.Add(new ListBoxItem { Content = c.Title, Padding = new Thickness(10, 6, 10, 6) });
        _list.SelectionChanged += (_, _) => Show(_list.SelectedIndex);
        _prev.Click += (_, _) => _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1);
        _next.Click += (_, _) => { if (_list.SelectedIndex < _chapters.Length - 1) _list.SelectedIndex++; else Close(); };

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _page };
        var buttons = new StackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 12, 16, 0) };
        buttons.Children.Add(_prev);
        buttons.Children.Add(_next);

        var right = new DockPanel { Margin = new Thickness(24, 0, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        right.Children.Add(buttons);
        right.Children.Add(_scroll);

        var left = new DockPanel();
        var wordmark = new System.Windows.Controls.Image { Source = SettingsWindow.WordmarkImage(cfg.Theme), Height = 40, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(8, 0, 0, 16) };
        DockPanel.SetDock(wordmark, Dock.Top);
        left.Children.Add(wordmark);
        left.Children.Add(_list);

        var root = new Grid { Margin = new Thickness(20) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(right, 1);
        root.Children.Add(left);
        root.Children.Add(right);
        Content = root;

        Theme.ApplyTo(this, cfg.Theme);
        _list.SelectedIndex = 0;
    }

    /// <summary>Opens at a chapter (0 = Welcome, 1 = the NVIDIA key).</summary>
    public void Open(int chapter) => _list.SelectedIndex = Math.Clamp(chapter, 0, _chapters.Length - 1);

    private void Show(int index)
    {
        if (index < 0) return;
        var c = _chapters[index];
        _page.Children.Clear();
        _page.Children.Add(new WpfTextBlock { Text = c.Title, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap });
        foreach (var b in c.Blocks) _page.Children.Add(Render(b));
        _prev.IsEnabled = index > 0;
        _next.Content = index < _chapters.Length - 1 ? "Next" : "Done";
        _scroll.ScrollToTop();
    }

    private static UIElement Render(Block b) => b switch
    {
        Para p => ParaBlock(p),
        Sub s => new WpfTextBlock { Text = s.Text, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6), TextWrapping = TextWrapping.Wrap },
        Note n => new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 4, 0, 10),
            Child = new WpfTextBlock { Text = n.Text, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 },
        }.WithResource(Border.BackgroundProperty, "ControlFillColorDefaultBrush"),
        Steps st => StepsBlock(st),
        Code code => CodeBlock(code),
        _ => new WpfTextBlock(),
    };

    private static UIElement ParaBlock(Para p)
    {
        var tb = new WpfTextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), LineHeight = 21 };
        tb.Inlines.Add(new Run(p.Text));
        if (p.Url != null)
        {
            var link = new Hyperlink(new Run(p.LinkText ?? p.Url)) { NavigateUri = new Uri(p.Url) };
            link.RequestNavigate += (_, e) => { try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { } };
            tb.Inlines.Add(link);
            tb.Inlines.Add(new Run("."));
        }
        return tb;
    }

    private static UIElement StepsBlock(Steps st)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < st.Items.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var n = new WpfTextBlock { Text = $"{i + 1}.", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
            n.SetResourceReference(WpfTextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            var t = new WpfTextBlock { Text = st.Items[i], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), LineHeight = 21 };
            Grid.SetRow(n, i);
            Grid.SetRow(t, i);
            Grid.SetColumn(t, 1);
            grid.Children.Add(n);
            grid.Children.Add(t);
        }
        return grid;
    }

    private static UIElement CodeBlock(Code code)
    {
        var copy = new WpfButton { Content = "Copy", Padding = new Thickness(10, 2, 10, 2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        copy.Click += (_, _) => { try { WpfClipboard.SetText(code.Text); copy.Content = "Copied"; } catch { } };
        var dock = new DockPanel();
        DockPanel.SetDock(copy, Dock.Right);
        dock.Children.Add(copy);
        dock.Children.Add(new WpfTextBlock
        {
            Text = code.Text, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        });
        return new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 8, 8), Margin = new Thickness(0, 2, 0, 12), Child = dock }
            .WithResource(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
    }
}

internal static class WpfExtensions
{
    public static T WithResource<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }
}
