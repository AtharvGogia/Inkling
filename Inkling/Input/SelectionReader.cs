using System.Runtime.InteropServices;
using static Inkling.Native;

namespace Inkling.Input;

/// <summary>
/// Reads the text selected in the target app (for the select-to-ask hotkey) by sending Ctrl+C,
/// then puts the previous clipboard contents back. Runs on the UI (STA) thread.
/// </summary>
internal static class SelectionReader
{
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    private const ushort VK_C = 0x43;

    /// <summary>The selected text, or null when nothing was selected (or it couldn't be read).</summary>
    public static async Task<string?> ReadAsync(nint target, int maxChars)
    {
        // Ctrl+C while the user still holds Alt from the hotkey would be Ctrl+Alt+C.
        await InputSender.WaitForModifiersReleasedAsync(1500);
        if (GetForegroundWindow() != target) return null;

        var saved = Snapshot();
        var before = GetClipboardSequenceNumber();
        InputSender.SendCtrlKey(VK_C);

        // Nothing selected → most apps leave the clipboard alone; give them a moment to answer.
        var until = Environment.TickCount64 + 300;
        while (GetClipboardSequenceNumber() == before && Environment.TickCount64 < until) await Task.Delay(10);
        if (GetClipboardSequenceNumber() == before) return null;

        string? text = null;
        try
        {
            await Task.Delay(20); // let the app finish writing all formats
            var data = Retry(Clipboard.GetDataObject);
            if (!IsWholeLineCopy(data) && data?.GetData(DataFormats.UnicodeText) is string s && s.Trim().Length > 0)
                text = s.Length > maxChars ? s[..maxChars] : s;
        }
        catch (Exception ex) { Log.Write("Selection read failed: " + ex.Message); }
        finally { Restore(saved); }
        return text;
    }

    /// <summary>VS Code copies the whole line when nothing is selected, and marks that case.</summary>
    private static bool IsWholeLineCopy(IDataObject? data)
    {
        try
        {
            if (data?.GetDataPresent("vscode-editor-data") != true) return false;
            var raw = data.GetData("vscode-editor-data");
            var json = raw is MemoryStream ms ? System.Text.Encoding.UTF8.GetString(ms.ToArray()) : raw?.ToString() ?? "";
            return json.Replace(" ", "").Contains("\"isFromEmptySelection\":true");
        }
        catch { return false; }
    }

    /// <summary>A copy of every format on the clipboard, so it can be put back after our Ctrl+C.</summary>
    private static DataObject? Snapshot()
    {
        try
        {
            var src = Retry(Clipboard.GetDataObject);
            if (src == null) return null;
            var copy = new DataObject();
            foreach (var f in src.GetFormats(false))
            {
                try
                {
                    if (src.GetData(f, false) is { } d) copy.SetData(f, false, d);
                }
                catch { /* formats that can't be read back (some delayed-render data) are skipped */ }
            }
            return copy;
        }
        catch { return null; }
    }

    private static void Restore(DataObject? saved)
    {
        try
        {
            if (saved == null || saved.GetFormats(false).Length == 0) Retry(() => { Clipboard.Clear(); return 0; });
            else Retry(() => { Clipboard.SetDataObject(saved, copy: true, retryTimes: 5, retryDelay: 20); return 0; });
        }
        catch (Exception ex) { Log.Write("Clipboard restore failed: " + ex.Message); }
    }

    private static T? Retry<T>(Func<T> f)
    {
        for (int i = 0; ; i++)
        {
            try { return f(); }
            catch (ExternalException) when (i < 5) { Thread.Sleep(15); } // clipboard held by another app
        }
    }
}
