using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Inkling;

/// <summary>A newer release on GitHub: its version, notes and installer download.</summary>
internal sealed record Release(Version Version, string Tag, string Notes, string PageUrl, string? InstallerUrl, string? InstallerName);

/// <summary>
/// Asks GitHub for the latest published release (drafts and pre-releases are ignored) and, on request,
/// downloads its installer and runs it. The installer closes Inkling, upgrades it and starts it again.
/// Only GitHub is contacted; nothing about the PC is sent.
/// </summary>
internal static class Updates
{
    private const string Api = "https://api.github.com/repos/AtharvGogia/Inkling/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Inkling", AppInfo.Version));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static Version Current => Parse(AppInfo.Version) ?? new Version(0, 0, 0);

    /// <summary>The latest release if it's newer than this build; null when up to date. Throws on network errors.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        using var res = await Http.GetAsync(Api, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no releases yet
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (Parse(tag) is not { } version || version <= Current) return null;

        string? url = null, name = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var n = a.GetProperty("name").GetString() ?? "";
            if (n.StartsWith("Inkling-Setup-", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name = n;
                url = a.GetProperty("browser_download_url").GetString();
                break;
            }
        }
        return new Release(version, tag,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            root.GetProperty("html_url").GetString() ?? "https://github.com/AtharvGogia/Inkling/releases",
            url, name);
    }

    /// <summary>Downloads the installer to %TEMP% and starts it; the installer takes over from there.</summary>
    public static async Task InstallAsync(Release release, IProgress<double>? progress, CancellationToken ct)
    {
        if (release.InstallerUrl is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This release has no installer to download. Open the release page instead.");

        var path = Path.Combine(Path.GetTempPath(), release.InstallerName ?? $"Inkling-Setup-{release.Version}.exe");
        using (var res = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? 0;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        Log.Write($"Update: downloaded {release.Tag}, starting the installer");
        // /SILENT shows only a progress bar; the installer closes Inkling and starts the new version.
        Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
    }

    internal static Version? Parse(string s)
    {
        s = s.Trim().TrimStart('v', 'V');
        var plus = s.IndexOfAny(['+', '-']);
        if (plus > 0) s = s[..plus];
        return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }
}
