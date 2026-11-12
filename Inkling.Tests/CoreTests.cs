using System.Text.RegularExpressions;
using Inkling;
using Inkling.Ai;
using Inkling.Input;
using Inkling.Ui;

namespace Inkling.Tests;

public class RiskGuardTests
{
    [Theory]
    [InlineData("rm -rf ~/old")]
    [InlineData("sudo rm -r build")]
    [InlineData("find . -type f -iname \"*.png\" -delete")]
    [InlineData("find /tmp -name '*.log' -exec rm {} +")]
    [InlineData("DROP TABLE users;")]
    [InlineData("DELETE FROM orders;")]
    [InlineData("UPDATE users SET admin = 1")]
    [InlineData("TRUNCATE TABLE logs")]
    [InlineData("git reset --hard HEAD~1")]
    [InlineData("git push --force origin main")]
    [InlineData("git clean -fd")]
    [InlineData(@"Remove-Item C:\temp -Recurse -Force")]
    [InlineData("rd /s /q build")]
    [InlineData("format D:")]
    [InlineData("docker system prune -a")]
    [InlineData("curl https://x.sh | sh")]
    [InlineData("shutdown /s /t 0")]
    [InlineData("Stop-Process -Name chrome")]
    [InlineData("mkfs.ext4 /dev/sdb1")]
    [InlineData("dd if=/dev/zero of=/dev/sda")]
    [InlineData(@"reg delete HKCU\Software\X /f")]
    public void Flags_destructive_commands(string answer) => Assert.NotNull(RiskGuard.Check(answer));

    [Theory]
    [InlineData("find ~ -type f -size +100M")]
    [InlineData("ls -la")]
    [InlineData("git reset --soft HEAD~1")]
    [InlineData("DELETE FROM orders WHERE created < '2020-01-01';")]
    [InlineData("UPDATE users SET admin = 1 WHERE id = 7;")]
    [InlineData("Get-ChildItem | Sort-Object Length -Descending | Select-Object -First 5")]
    [InlineData("docker ps -a")]
    [InlineData("chmod +x deploy.sh")]
    [InlineData("Ottawa")]
    [InlineData("=SUM(A1:A10)")]
    [InlineData("Restart-Service Spooler")]
    [InlineData("mkdir -p src/rm-old")]
    [InlineData("echo shutdown later")]
    public void Leaves_safe_answers_alone(string answer) => Assert.Null(RiskGuard.Check(answer));
}

public class SnippetTests
{
    private static AppConfig Config(params (string Trigger, string Text)[] snippets) =>
        new() { Snippets = snippets.Select(s => new Snippet { Trigger = s.Trigger, Text = s.Text }).ToList() };

    [Fact]
    public void Matches_ignoring_case_and_extra_spaces()
    {
        var cfg = Config(("my ip", "curl ifconfig.me"));
        Assert.Equal("curl ifconfig.me", Snippets.Expand(cfg, "  My   IP "));
    }

    [Fact]
    public void No_match_means_ask_the_model() => Assert.Null(Snippets.Expand(Config(("shrug", "x")), "shrug please"));

    [Fact]
    public void Fills_in_the_date()
    {
        var text = Snippets.Expand(Config(("today", "{date}")), "today");
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", text);
    }

    [Fact]
    public void Empty_triggers_never_match() => Assert.Null(Snippets.Expand(Config(("", "x")), ""));
}

public class OutputCleanerTests
{
    [Theory]
    [InlineData("```bash\nls -la\n```", "ls -la")]
    [InlineData("`ls -la`", "ls -la")]
    [InlineData("$ ls -la", "ls -la")]
    [InlineData("PS C:\\> Get-Service", "Get-Service")]
    [InlineData("\"Paris\"", "Paris")]
    [InlineData("<think>the user wants a city</think>\nParis", "Paris")]
    public void Strips_decoration(string raw, string expected) => Assert.Equal(expected, OutputCleaner.Clean(raw, singleLine: true));

    [Fact]
    public void Keeps_only_the_first_line_when_asked() =>
        Assert.Equal("first", OutputCleaner.Clean("first\nsecond", singleLine: true));

    [Fact]
    public void Unfinished_thinking_is_no_answer() => Assert.Equal("", OutputCleaner.Clean("<think>still going", singleLine: true));
}

public class HotkeyTests
{
    [Fact]
    public void Parses_and_prints_back()
    {
        var hk = Hotkey.Parse("ctrl+alt+space");
        Assert.True(hk.Ctrl && hk.Alt && !hk.Shift && !hk.Win);
        Assert.Equal(Keys.Space, hk.Key);
        Assert.Equal("Ctrl+Alt+Space", hk.ToString());
    }

    [Fact]
    public void Digits_round_trip() => Assert.Equal("Ctrl+Alt+5", Hotkey.Parse("Ctrl+Alt+5").ToString());

    [Fact]
    public void Needs_ctrl_alt_or_win() => Assert.Throws<FormatException>(() => Hotkey.Parse("Shift+A"));

    [Fact]
    public void Needs_a_main_key() => Assert.Throws<FormatException>(() => Hotkey.Parse("Ctrl+Alt"));
}

public class PromptTests
{
    [Theory]
    [InlineData("SENTENCE", "sentence")]
    [InlineData("nonsense", "short")]
    [InlineData(null, "short")]
    public void Normalizes_lengths(string? input, string expected) => Assert.Equal(expected, Prompt.NormalizeLength(input));

    [Fact]
    public void Message_has_context_length_request_and_rejected_answers()
    {
        var msg = Prompt.Build(new AppConfig { AnswerLength = "short" }, "Context: X", "word for happy", ["joyful"]);
        Assert.Contains("Context: X", msg);
        Assert.Contains("Length wanted:", msg);
        Assert.Contains("Request: word for happy", msg);
        Assert.Contains("- joyful", msg);
    }
}

public class ContextTests
{
    [Theory]
    [InlineData("WindowsTerminal", "Windows PowerShell", "PowerShell")]
    [InlineData("WindowsTerminal", "atharv@PC: ~", "bash")]
    [InlineData("EXCEL", "Budget.xlsx - Excel", "spreadsheet formula")]
    [InlineData("Notepad", "notes.txt - Notepad", "short plain-text answer")]
    public void Picks_the_answer_type_for_the_app(string process, string title, string expected) =>
        Assert.Contains(expected, ForegroundContext.HintFor(process, title, new AppConfig()));
}

public class UpdateTests
{
    [Theory]
    [InlineData("v0.4.2", "0.4.2")]
    [InlineData("0.5", "0.5.0")]
    [InlineData("v1.2.3-beta", "1.2.3")]
    public void Reads_release_tags(string tag, string expected) => Assert.Equal(Version.Parse(expected), Updates.Parse(tag));

    [Fact]
    public void Ignores_tags_that_are_not_versions() => Assert.Null(Updates.Parse("latest"));
}

public class BadgeTests
{
    [Fact]
    public void Ready_blinks_its_cursor()
    {
        Assert.Equal("i_rdy", BadgeIndicator.TextAt(BadgeIndicator.Mode.Ready, 0.1));
        Assert.Equal("i rdy", BadgeIndicator.TextAt(BadgeIndicator.Mode.Ready, 0.6));
    }

    [Fact]
    public void Thinking_says_thnk_with_a_faster_cursor()
    {
        Assert.Equal("i_thnk", BadgeIndicator.TextAt(BadgeIndicator.Mode.Thinking, 0.1));
        Assert.Equal("i thnk", BadgeIndicator.TextAt(BadgeIndicator.Mode.Thinking, 0.3));
    }

    [Fact]
    public void Done_shows_fin_then_hides()
    {
        Assert.Equal("i_fin", BadgeIndicator.TextAt(BadgeIndicator.Mode.Done, 0.5));
        Assert.Null(BadgeIndicator.TextAt(BadgeIndicator.Mode.Done, 1.5));
    }

    [Fact]
    public void Error_shows_then_hides()
    {
        Assert.Equal("i_err", BadgeIndicator.TextAt(BadgeIndicator.Mode.Error, 1.0));
        Assert.Null(BadgeIndicator.TextAt(BadgeIndicator.Mode.Error, 2.5));
    }

    [Fact]
    public void No_state_is_wider_than_the_badge()
    {
        foreach (var mode in Enum.GetValues<BadgeIndicator.Mode>())
            Assert.True((BadgeIndicator.TextAt(mode, 0) ?? "").Length <= BadgeIndicator.Widest.Length);
    }

    [Fact]
    public void Each_state_has_its_own_colour()
    {
        var colours = Enum.GetValues<BadgeIndicator.Mode>().Select(m => BadgeIndicator.ColorFor(m).ToArgb()).ToList();
        Assert.Equal(colours.Count, colours.Distinct().Count());
    }
}

public class BenchQuestionTests
{
    [Fact]
    public void Every_grader_is_a_valid_regex()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bench", "questions.json");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        foreach (var q in doc.RootElement.GetProperty("questions").EnumerateArray())
            foreach (var key in new[] { "expect", "require" })
                if (q.TryGetProperty(key, out var list))
                    foreach (var p in list.EnumerateArray())
                        _ = new Regex(p.GetString()!);
    }
}
