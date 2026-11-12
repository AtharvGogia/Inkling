using System.Text.RegularExpressions;

namespace Inkling.Ai;

/// <summary>
/// A request that wants code written, and in which language if that's known. Found from the request itself
/// ("... in rust", "rust function that ..."), or, for a task-like request, from where it's typed: a code
/// playground page (play.rust-lang.org) or a code file (main.rs).
/// </summary>
public sealed record CodeIntent(string? Language, string? Source)
{
    /// <summary>"Rust (the page is the Rust Playground)", "Python", or null when the model should pick.</summary>
    public string? Describe() => Language is null ? null : Source is null ? Language : $"{Language} ({Source})";
}

public static partial class CodeDetector
{
    /// <summary>Language names as people type them, and the name to use. Order matters: longer names first.</summary>
    private static readonly (string Pattern, string Name)[] Languages =
    [
        ("rust", "Rust"), ("python3?|py", "Python"), ("javascript|node(?:\\.?js)?|js", "JavaScript"), ("typescript|ts", "TypeScript"),
        ("c#|csharp|c sharp", "C#"), ("c\\+\\+|cpp", "C++"), ("java", "Java"), ("golang|go", "Go"), ("kotlin", "Kotlin"),
        ("swift", "Swift"), ("php", "PHP"), ("ruby", "Ruby"), ("sql|mysql|postgres(?:ql)?|sqlite|t-sql", "SQL"),
        ("bash|shell|zsh", "Bash"), ("powershell", "PowerShell"), ("html", "HTML"), ("css", "CSS"), ("lua", "Lua"),
        ("dart", "Dart"), ("scala", "Scala"), ("haskell", "Haskell"), ("c", "C"), ("r", "R"),
    ];

    private static readonly string LangAlternation = string.Join("|", Languages.Select(l => l.Pattern));

    /// <summary>Names too common as plain words ("starts with r", "vitamin c", "with shell") to count after with/for.</summary>
    private static readonly string LangAlternationStrict = string.Join("|",
        Languages.Where(l => l.Name is not ("C" or "R" or "Bash")).Select(l => l.Pattern).Append("bash"));

    // "in rust", "using python", "written in go", "for typescript"
    private static readonly Regex NamedTarget = new(
        $@"\b(?:(?:in|using)\s+({LangAlternation})|(?:with|for)\s+({LangAlternationStrict}))(?![\w+#])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // "rust function", "python script", "sql query"
    private static readonly Regex NamedNoun = new($@"(?<![\w+#])({LangAlternation})\s+(?:code|function|fn|program|script|snippet|class|method|struct|enum|one-?liner|query|loop|example|macro|module|trait|closure|lambda)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "write a function that...", "implement a class for..." (language left to the model)
    [GeneratedRegex(@"\b(?:write|create|make|implement|generate|code|build|give me)\b.*\b(?:function|class|method|program|struct|enum|interface|component|endpoint|algorithm|regex|unit tests?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex VerbNoun();

    // A task, not a question: something to do.
    [GeneratedRegex(@"\b(?:create|write|make|implement|reverse|sort|parse|convert|build|add|generate|print|read|fetch|declare|initiali[sz]e|loop|iterate|count|find|filter|map|split|join|calculate|compute|check|validate|open|send|get|set|remove|delete|append|merge|format|hash|encode|decode|serialize|deserialize|spawn|connect|define a function|swap|sum|concatenate|compare)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TaskVerb();

    [GeneratedRegex(@"^\s*(?:what|why|who|when|which|explain|is|are|does|do you|tell me about|meaning of|difference between)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Question();

    /// <summary>File extensions that are code, and their language.</summary>
    internal static readonly Dictionary<string, string> CodeFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        [".rs"] = "Rust", [".py"] = "Python", [".ipynb"] = "Python", [".js"] = "JavaScript", [".mjs"] = "JavaScript", [".jsx"] = "JavaScript (React)",
        [".ts"] = "TypeScript", [".tsx"] = "TypeScript (React)", [".cs"] = "C#", [".java"] = "Java", [".kt"] = "Kotlin", [".c"] = "C",
        [".h"] = "C", [".cpp"] = "C++", [".go"] = "Go", [".rb"] = "Ruby", [".php"] = "PHP", [".swift"] = "Swift", [".ps1"] = "PowerShell",
        [".psm1"] = "PowerShell", [".sh"] = "Bash", [".sql"] = "SQL", [".lua"] = "Lua", [".dart"] = "Dart", [".r"] = "R",
        [".html"] = "HTML", [".css"] = "CSS", [".scss"] = "SCSS", [".vue"] = "Vue", [".bat"] = "Windows batch", [".cmd"] = "Windows batch",
        [".scala"] = "Scala", [".hs"] = "Haskell",
    };

    /// <summary>Code playgrounds: host, optional path start, language (null = depends on the page) and a name.</summary>
    internal static readonly (string Host, string Path, string? Language, string Name)[] CodeSites =
    [
        ("play.rust-lang.org", "", "Rust", "the Rust Playground"),
        ("go.dev", "/play", "Go", "the Go Playground"),
        ("typescriptlang.org", "/play", "TypeScript", "the TypeScript Playground"),
        ("dotnetfiddle.net", "", "C#", ".NET Fiddle"),
        ("sharplab.io", "", "C#", "SharpLab"),
        ("jsfiddle.net", "", "JavaScript", "JSFiddle"),
        ("codepen.io", "", "JavaScript", "CodePen"),
        ("colab.research.google.com", "", "Python", "Google Colab"),
        ("jupyter.org", "/try", "Python", "Jupyter"),
        ("sqlfiddle.com", "", "SQL", "SQL Fiddle"),
        ("db-fiddle.com", "", "SQL", "DB Fiddle"),
        ("godbolt.org", "", "C++", "Compiler Explorer"),
        ("onecompiler.com", "", null, "OneCompiler"),
        ("programiz.com", "", null, "Programiz"),
        ("onlinegdb.com", "", null, "OnlineGDB"),
        ("replit.com", "", null, "Replit"),
        ("leetcode.com", "/problems", null, "LeetCode"),
        ("github.dev", "", null, "github.dev"),
        ("vscode.dev", "", null, "vscode.dev"),
        ("stackblitz.com", "", null, "StackBlitz"),
        ("codesandbox.io", "", null, "CodeSandbox"),
    ];

    /// <summary>Page titles that identify a playground when the address isn't known (address off, or private window).</summary>
    private static readonly (string Title, string Language, string Name)[] CodeTitles =
    [
        ("Rust Playground", "Rust", "the Rust Playground"), ("Go Playground", "Go", "the Go Playground"),
        ("TS Playground", "TypeScript", "the TypeScript Playground"), ("Compiler Explorer", "C++", "Compiler Explorer"),
        ("JSFiddle", "JavaScript", "JSFiddle"), ("CodePen", "JavaScript", "CodePen"), ("Colab", "Python", "Google Colab"),
    ];

    /// <summary>The language a page is for: (language or null, site name). Null when it isn't a code site.</summary>
    internal static (string? Language, string Name)? Site(string? url, string? title)
    {
        if (url is { Length: > 0 })
        {
            var slash = url.IndexOf('/');
            var host = slash < 0 ? url : url[..slash];
            var path = slash < 0 ? "" : url[slash..];
            foreach (var s in CodeSites)
                if ((host.Equals(s.Host, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + s.Host, StringComparison.OrdinalIgnoreCase))
                    && path.StartsWith(s.Path, StringComparison.OrdinalIgnoreCase))
                    return (s.Language ?? LanguageInPath(path), s.Name);
        }
        if (title is { Length: > 0 })
            foreach (var t in CodeTitles)
                if (title.Contains(t.Title, StringComparison.OrdinalIgnoreCase)) return (t.Language, t.Name);
        return null;
    }

    /// <summary>"/rust", "/python-programming/online-compiler", "/online_c++_compiler" → the language named in it.</summary>
    private static string? LanguageInPath(string path)
    {
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(2))
        {
            var token = part.Split('-', '_')[0].ToLowerInvariant();
            if (token == "online" && part.Split('-', '_') is { Length: > 1 } bits) token = bits[1].ToLowerInvariant();
            var hit = NameOf(token);
            if (hit != null) return hit;
        }
        return null;
    }

    private static string? NameOf(string token)
    {
        if (token is "nodejs" or "node") return "JavaScript";
        foreach (var (pattern, name) in Languages)
            if (Regex.IsMatch(token, $"^(?:{pattern})$", RegexOptions.IgnoreCase)) return name;
        return null;
    }

    /// <summary>
    /// Whether the request wants code. <paramref name="url"/> and <paramref name="title"/> describe the page (browsers),
    /// <paramref name="extension"/> the open file (other apps). A language named in the request wins over the page's.
    /// </summary>
    public static CodeIntent? Detect(string request, string? url = null, string? title = null, string? extension = null)
    {
        var named = NamedTarget.Match(request) is { Success: true } a ? a : NamedNoun.Match(request);
        if (named.Success)
        {
            var word = (named.Groups[1].Success ? named.Groups[1].Value : named.Groups[2].Value).ToLowerInvariant().Replace(" ", "");
            return new CodeIntent(NameOf(word) ?? word, null);
        }

        bool task = TaskVerb().IsMatch(request) && !Question().IsMatch(request);
        if (task && Site(url, title) is { } site) return new CodeIntent(site.Language, "the page is " + site.Name);
        if (task && extension is { Length: > 0 } && CodeFiles.TryGetValue(extension, out var fileLang))
            return new CodeIntent(fileLang, $"the open file is {extension}");
        if (VerbNoun().IsMatch(request)) return new CodeIntent(null, null);
        return null;
    }
}
