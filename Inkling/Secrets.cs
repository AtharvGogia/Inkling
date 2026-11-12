using System.Runtime.InteropServices;
using System.Text;

namespace Inkling;

/// <summary>
/// The NIM API key, kept in Windows Credential Manager (generic credential "Inkling:nim"), never in
/// config.json or the log. Falls back to the NVIDIA_API_KEY environment variable.
/// </summary>
internal static class Secrets
{
    private const string Target = "Inkling:nim";
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, int type, int flags, out nint credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref CREDENTIAL credential, int flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, int type, int flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);

    /// <summary>Where the key in use comes from, for the tray ("stored", "environment" or null).</summary>
    public static string? NimKeySource() =>
        ReadStored() != null ? "stored" : Env() != null ? "environment" : null;

    public static string? NimKey() => ReadStored() ?? Env();

    private static string? Env()
    {
        var v = Environment.GetEnvironmentVariable("NVIDIA_API_KEY");
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static string? ReadStored()
    {
        if (!CredReadW(Target, CRED_TYPE_GENERIC, 0, out var p)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(p);
            if (cred.CredentialBlobSize == 0) return null;
            var s = Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2);
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        finally { CredFree(p); }
    }

    public static void SaveNimKey(string key)
    {
        var bytes = Encoding.Unicode.GetBytes(key.Trim());
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = Target,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = "Inkling",
                Comment = "NVIDIA NIM API key used by Inkling",
            };
            if (!CredWriteW(ref cred, 0)) throw new InvalidOperationException($"Could not save the key (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Array.Clear(bytes);
            Marshal.FreeHGlobal(blob);
        }
    }

    public static void DeleteNimKey() => CredDeleteW(Target, CRED_TYPE_GENERIC, 0);
}
