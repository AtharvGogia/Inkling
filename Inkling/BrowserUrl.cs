using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Automation;
using static Inkling.Native;

namespace Inkling;

/// <summary>
/// The address of the page open in a browser, read from its address bar through UI Automation. Only the site and
/// path are kept (<see cref="Clean"/>): the query string and fragment, where search terms and tokens live, are
/// dropped. Private windows are skipped.
/// </summary>
internal static class BrowserUrl
{
    internal static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "opera", "opera_gx", "vivaldi", "arc", "chromium",
        "firefox", "floorp", "librewolf", "waterfox", "zen",
    };

    public static bool IsBrowser(string process) => Browsers.Contains(process);

    /// <summary>Private, Incognito and InPrivate windows say so in their title.</summary>
    public static bool IsPrivate(string title) =>
        title.Contains("InPrivate", StringComparison.OrdinalIgnoreCase)
        || title.Contains("Incognito", StringComparison.OrdinalIgnoreCase)
        || title.Contains("Private Browsing", StringComparison.OrdinalIgnoreCase);

    /// <summary>The last address per window, keyed with the title (which changes with the tab), kept for 30 s.</summary>
    private static readonly ConcurrentDictionary<nint, (string Title, string? Url, long At)> Cache = new();

    /// <summary>
    /// Starts reading the address on a pool thread and returns at once (it's called from the keyboard hook).
    /// The result is null for non-browsers, private windows, and anything that isn't a web page.
    /// </summary>
    public static Task<string?> StartLookup(nint hwnd) => hwnd == 0 ? Task.FromResult<string?>(null) : Task.Run(() => Lookup(hwnd));

    internal static string? Lookup(nint hwnd)
    {
        try
        {
            if (!IsBrowser(ForegroundContext.ProcessName(hwnd))) return null;
            var title = GetWindowTitle(hwnd);
            if (IsPrivate(title)) return null;
            if (Cache.TryGetValue(hwnd, out var c) && c.Title == title && Environment.TickCount64 - c.At < 30_000) return c.Url;
            var url = Clean(ReadAddressBar(hwnd));
            Cache[hwnd] = (title, url, Environment.TickCount64);
            return url;
        }
        catch (Exception ex)
        {
            Log.Write("Page address lookup failed: " + ex.Message);
            return null;
        }
    }

    private static readonly Condition EditControl = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit);

    /// <summary>Firefox-based browsers name the field "urlbar-input"; Chromium-based ones "Address and search bar".</summary>
    private static readonly Condition AddressBar = new AndCondition(EditControl, new OrCondition(
        new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input"),
        new PropertyCondition(AutomationElement.NameProperty, "Address and search bar")));

    private static string? ReadAddressBar(nint hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);
        // The toolbar comes before the page in the tree, so these searches stop long before the page content.
        var field = root.FindFirst(TreeScope.Descendants, AddressBar);
        if (field == null)
        {
            // A browser in another language names the field differently: take the first text field in the toolbar.
            var toolbar = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolBar));
            field = toolbar?.FindFirst(TreeScope.Descendants, EditControl);
        }
        return field != null && field.TryGetCurrentPattern(ValuePattern.Pattern, out var p) ? ((ValuePattern)p).Current.Value : null;
    }

    /// <summary>
    /// "https://github.com/a/b/issues/3?q=x#top" → "github.com/a/b/issues/3". Null for anything that isn't an http(s)
    /// page: browser pages (chrome://, about:), local files, or search text being typed into the address bar.
    /// </summary>
    internal static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.Any(char.IsWhiteSpace)) return null;                                    // search text, not an address
        if (!raw.Contains("://", StringComparison.Ordinal))
        {
            if (raw.Contains(':') && !raw.Contains('.')) return null;                    // about:blank, edge:settings
            raw = "https://" + raw;                                                       // browsers hide the scheme
        }
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;
        if (!uri.Host.Contains('.') && uri.Host != "localhost") return null;
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        var result = host + (uri.IsDefaultPort ? "" : ":" + uri.Port) + uri.AbsolutePath.TrimEnd('/');
        return result.Length > 150 ? result[..150] + "…" : result;
    }

    /// <summary>--url-test: once a second, the address found in the foreground window. Sends no input.</summary>
    internal static int SelfTest(int seconds)
    {
        Console.WriteLine("Switch to a browser window; its page address is read once a second.");
        for (int i = 0; i < seconds; i++)
        {
            Thread.Sleep(1000);
            var fg = GetForegroundWindow();
            Cache.Clear();
            var sw = Stopwatch.StartNew();
            var url = Lookup(fg);
            Console.WriteLine($"{ForegroundContext.ProcessName(fg),-14} {sw.ElapsedMilliseconds,5} ms  {url ?? "-"}");
        }
        return 0;
    }
}
