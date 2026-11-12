using System.Text.RegularExpressions;

namespace Inkling;

/// <summary>
/// Spots answers that would destroy data or disrupt the PC if run, so they can be shown and confirmed
/// before being typed. Inkling never runs anything itself, but a typed command is one Enter away.
/// </summary>
internal static partial class RiskGuard
{
    private static readonly (Regex Pattern, string Why)[] Rules =
    [
        (R(@"\brm\s+(-[a-z]*[rf][a-z]*\s+|--recursive\s+|--force\s+)+"), "deletes files (rm -r/-f)"),
        (R(@"\bfind\b.*\s-delete\b|\bfind\b.*-exec\s+rm\b"), "deletes every file it finds"),
        (R(@"\bshred\b|\bwipefs\b|\bmkfs(\.\w+)?\b|\bdd\s+.*\bof=/dev/"), "wipes a disk or files"),
        (R(@">\s*/dev/sd[a-z]|\bchmod\s+-R\s+0?777\s+/(\s|$)|\bchown\s+-R\s+\S+\s+/(\s|$)"), "changes the whole system"),
        (R(@":\(\)\s*\{\s*:\|:&\s*\}\s*;\s*:"), "fork bomb"),
        (R(@"\bkill\s+-9\s+-1\b|\bkillall\b|\bpkill\b|\bStop-Process\b|\btaskkill\b.*\s/f\b"), "kills processes"),
        (R(@"(^|[;&|]\s*|\bsudo\s+)(shutdown|reboot|halt|poweroff)\b|\b(Stop|Restart)-Computer\b"), "shuts down or restarts the PC"),
        (R(@"\bRemove-Item\b.*-(Recurse|Force)\b|\b(rm|ri|del|erase|rd|rmdir)\b\s+.*(/s|/q|-Recurse|-Force)\b"), "deletes files or folders"),
        (R(@"\bformat\s+[a-z]:|\bFormat-Volume\b|\bClear-Disk\b|\bdiskpart\b"), "formats a drive"),
        (R(@"\breg\s+delete\b|\bRemove-ItemProperty\b.*HK(LM|CU)"), "deletes registry entries"),
        (R(@"\bDROP\s+(TABLE|DATABASE|SCHEMA)\b|\bTRUNCATE\s+(TABLE\s+)?\w+"), "drops or empties database tables"),
        (R(@"\bDELETE\s+FROM\s+[\w.""`\[\]]+\s*;?\s*$"), "deletes every row (no WHERE)"),
        (R(@"\bUPDATE\s+[\w.""`\[\]]+\s+SET\b(?!.*\bWHERE\b)"), "updates every row (no WHERE)"),
        (R(@"\bgit\s+(reset\s+--hard|clean\s+-[a-z]*f|push\b.*(--force|\s-f\b)|branch\s+-D\b|checkout\s+--\s+\.|restore\s+\.)"), "throws away git work"),
        (R(@"\bdocker\s+(system|volume|image|container)\s+prune\b|\bdocker\s+rm\s+-f\b"), "deletes Docker data"),
        (R(@"\bcurl\b.*\|\s*(sudo\s+)?(ba|z)?sh\b|\bwget\b.*\|\s*(sudo\s+)?(ba|z)?sh\b|\biex\s*\(.*(iwr|Invoke-WebRequest|DownloadString)"), "runs a script straight from the internet"),
    ];

    private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    /// <summary>A short reason when the answer looks risky, else null.</summary>
    public static string? Check(string answer)
    {
        foreach (var (pattern, why) in Rules)
        {
            try
            {
                if (pattern.IsMatch(answer)) return why;
            }
            catch (RegexMatchTimeoutException) { }
        }
        return null;
    }
}
