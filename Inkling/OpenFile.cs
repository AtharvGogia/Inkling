namespace Inkling;

/// <summary>
/// The file open in the target window, read from its title: almost every app puts the file name there
/// ("report.docx - Word", "*notes.txt - Notepad", "app.py - project - Visual Studio Code"). Only the file name is
/// ever returned, never its folder, since paths carry the user name and folder layout.
/// </summary>
internal static class OpenFile
{
    /// <summary>A file name, or <see cref="Unsaved"/> for a new document that has none yet.</summary>
    public sealed record Result(string? Name, bool Unsaved)
    {
        public string Extension => Name is null ? "" : Path.GetExtension(Name).ToLowerInvariant();

        /// <summary>For the context sent to the model: <c>file "notes.ps1" (a PowerShell script)</c>.</summary>
        public string Describe() =>
            Unsaved || Name is null ? "a new, unsaved document"
            : Types.TryGetValue(Extension, out var type) ? $"file \"{Name}\" ({type})" : $"file \"{Name}\"";
    }

    /// <summary>Known extensions and what to call them. Only titles naming one of these count as a file.</summary>
    internal static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".docx"] = "a Word document", [".doc"] = "a Word document", [".rtf"] = "a rich-text document", [".odt"] = "a text document",
        [".xlsx"] = "an Excel workbook", [".xlsm"] = "an Excel workbook", [".xls"] = "an Excel workbook", [".ods"] = "a spreadsheet",
        [".csv"] = "a CSV file", [".tsv"] = "a tab-separated file",
        [".pptx"] = "a PowerPoint presentation", [".ppt"] = "a PowerPoint presentation",
        [".pdf"] = "a PDF", [".txt"] = "a text file", [".md"] = "a Markdown file", [".log"] = "a log file",
        [".py"] = "a Python script", [".ipynb"] = "a Jupyter notebook", [".js"] = "a JavaScript file", [".mjs"] = "a JavaScript file",
        [".ts"] = "a TypeScript file", [".tsx"] = "a React TypeScript file", [".jsx"] = "a React JavaScript file",
        [".cs"] = "a C# file", [".java"] = "a Java file", [".kt"] = "a Kotlin file", [".c"] = "a C file", [".h"] = "a C/C++ header",
        [".cpp"] = "a C++ file", [".go"] = "a Go file", [".rs"] = "a Rust file", [".rb"] = "a Ruby file", [".php"] = "a PHP file",
        [".swift"] = "a Swift file", [".ps1"] = "a PowerShell script", [".psm1"] = "a PowerShell module", [".sh"] = "a shell script",
        [".bat"] = "a Windows batch file", [".cmd"] = "a Windows batch file", [".sql"] = "a SQL file", [".json"] = "a JSON file",
        [".yaml"] = "a YAML file", [".yml"] = "a YAML file", [".toml"] = "a TOML file", [".ini"] = "an INI file", [".xml"] = "an XML file",
        [".html"] = "an HTML file", [".css"] = "a CSS file", [".scss"] = "a SCSS file", [".tex"] = "a LaTeX document",
        [".r"] = "an R script", [".lua"] = "a Lua script", [".dart"] = "a Dart file", [".vue"] = "a Vue component",
        [".png"] = "an image", [".jpg"] = "an image", [".jpeg"] = "an image", [".gif"] = "an image", [".psd"] = "a Photoshop image",
    };

    /// <summary>Office apps leave the extension out when Windows hides extensions, so their usual one is assumed.</summary>
    private static readonly Dictionary<string, (string Ext, string NewDoc)> Office = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WINWORD"] = (".docx", "Document"),
        ["EXCEL"] = (".xlsx", "Book"),
        ["POWERPNT"] = (".pptx", "Presentation"),
    };

    private static readonly string[] Separators = [" - ", " — ", " – ", " | "];

    /// <summary>The file named in a window title, or null when the title doesn't name one.</summary>
    public static Result? FromTitle(string process, string title)
    {
        title = title.Trim();
        if (title.Length == 0) return null;
        var segments = title.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanSegment).Where(s => s.Length > 0).ToList();
        if (segments.Count == 0) return null;

        if (Office.TryGetValue(process, out var office))
        {
            var first = segments[0];
            if (IsNewDocument(first, office.NewDoc)) return new Result(null, true);
            var name = FileName(first);
            if (name.Length == 0) return null;
            return new Result(Types.ContainsKey(Path.GetExtension(name)) ? name : name + office.Ext, false);
        }
        if (segments[0].StartsWith("Untitled", StringComparison.OrdinalIgnoreCase)
            && (process.Equals("Notepad", StringComparison.OrdinalIgnoreCase) || process.Equals("notepad++", StringComparison.OrdinalIgnoreCase)
                || process.Equals("Code", StringComparison.OrdinalIgnoreCase) || process.Equals("Cursor", StringComparison.OrdinalIgnoreCase)))
            return new Result(null, true);

        // Everything else: the first part of the title that is a file name with a known extension.
        foreach (var s in segments)
        {
            var name = FileName(s);
            if (name.Length > 0 && Types.ContainsKey(Path.GetExtension(name))) return new Result(name, false);
        }
        return null;
    }

    /// <summary>Drops unsaved markers ("*", "●"), Office status ("• Saved"), Photoshop zoom ("@ 66.7% (RGB/8)").</summary>
    private static string CleanSegment(string s)
    {
        s = s.TrimStart('*', '●', '•', ' ').TrimEnd('*', ' ');
        foreach (var cut in new[] { " • ", " @ ", "  [" , " [Compatibility Mode]", " (Compatibility Mode)", " [Read-Only]", " (Read-Only)" })
        {
            var i = s.IndexOf(cut, StringComparison.OrdinalIgnoreCase);
            if (i > 0) s = s[..i];
        }
        return s.Trim();
    }

    /// <summary>The last part of a path, so a folder is never sent; capped at 100 characters.</summary>
    private static string FileName(string s)
    {
        var i = s.LastIndexOfAny(['\\', '/']);
        var name = (i >= 0 ? s[(i + 1)..] : s).Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "";
        return name.Length > 100 ? name[..100] : name;
    }

    private static bool IsNewDocument(string s, string prefix) =>
        s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && s.Length > prefix.Length && s[prefix.Length..].All(char.IsDigit);
}
