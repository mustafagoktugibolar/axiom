using System.Text.RegularExpressions;

namespace Axiom.Domain.Policy.Rules.Support;

internal enum SourceLanguage
{
    None,
    CSharp,
    JavaScript,
    Python,
    Java,
    Kotlin,
    Go,
    MSBuild,
}

/// <summary>A reference from a source file to a namespace, module, package path or project.</summary>
internal sealed record SourceReference(int Line, string Target, string Form);

/// <summary>
/// Extracts dependency references from source text without a compiler: C# <c>using</c> directives and
/// fully-qualified names, TypeScript/JavaScript <c>import</c>/<c>export from</c>/<c>require</c>,
/// Python <c>import</c>/<c>from</c>, Java and Kotlin <c>import</c> and qualified names, Go
/// <c>import</c>, and MSBuild <c>ProjectReference</c>. Comments are ignored, and so are string
/// literals wherever the reference itself is not a string. Every pattern runs on the linear-time
/// (non-backtracking) regex engine, so untrusted input cannot cause catastrophic matching.
/// </summary>
internal static partial class SourceScanner
{
    private const RegexOptions Linear = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private const int TimeoutMilliseconds = 5000;

    public static SourceLanguage Detect(string path)
    {
        var dot = path.LastIndexOf('.');
        var extension = dot < 0 ? string.Empty : path[dot..].ToLowerInvariant();
        return extension switch
        {
            ".cs" => SourceLanguage.CSharp,
            ".ts" or ".tsx" or ".mts" or ".cts" or ".js" or ".jsx" or ".mjs" or ".cjs" => SourceLanguage.JavaScript,
            ".py" or ".pyi" => SourceLanguage.Python,
            ".java" => SourceLanguage.Java,
            ".kt" or ".kts" => SourceLanguage.Kotlin,
            ".go" => SourceLanguage.Go,
            ".csproj" or ".fsproj" or ".vbproj" => SourceLanguage.MSBuild,
            _ => SourceLanguage.None,
        };
    }

    /// <summary>
    /// References in the given lines, in line order. <paramref name="isFragment"/> is true when the lines are
    /// isolated added lines rather than a whole file, in which case multi-line constructs are read leniently.
    /// </summary>
    public static IReadOnlyList<SourceReference> References(SourceLanguage language, IEnumerable<AddedLine> lines, bool isFragment)
    {
        var references = new List<SourceReference>();
        if (language == SourceLanguage.None)
        {
            return references;
        }

        var sanitizer = new SourceSanitizer(language);
        var inGoImportBlock = false;
        foreach (var line in lines)
        {
            var (code, text) = sanitizer.Next(line.Text);
            switch (language)
            {
                case SourceLanguage.CSharp:
                    ReadCSharp(line.Number, code, references);
                    break;
                case SourceLanguage.Java or SourceLanguage.Kotlin:
                    ReadJvm(line.Number, code, references);
                    break;
                case SourceLanguage.Python:
                    ReadPython(line.Number, code, references);
                    break;
                case SourceLanguage.JavaScript:
                    ReadJavaScript(line.Number, code, text, references);
                    break;
                case SourceLanguage.Go:
                    inGoImportBlock = ReadGo(line.Number, code, text, inGoImportBlock, isFragment, references);
                    break;
                case SourceLanguage.MSBuild:
                    foreach (Match match in MSBuildProjectReference().Matches(text))
                    {
                        references.Add(new SourceReference(line.Number, ProjectName(match.Groups[1].Value), "project reference"));
                    }

                    break;
            }
        }

        return references;
    }

    private static string ProjectName(string include)
    {
        var name = include.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    private static void ReadCSharp(int number, string code, List<SourceReference> references)
    {
        var directive = CSharpUsing().Match(code);
        if (directive.Success)
        {
            references.Add(new SourceReference(number, directive.Groups[1].Value.TrimEnd('.'), "using"));
            return;
        }

        if (!code.AsSpan().TrimStart().StartsWith("namespace ", StringComparison.Ordinal))
        {
            AddQualifiedNames(number, code, references);
        }
    }

    private static void ReadJvm(int number, string code, List<SourceReference> references)
    {
        var import = JvmImport().Match(code);
        if (import.Success)
        {
            references.Add(new SourceReference(number, import.Groups[1].Value.TrimEnd('.'), "import"));
            return;
        }

        if (!code.AsSpan().TrimStart().StartsWith("package ", StringComparison.Ordinal))
        {
            AddQualifiedNames(number, code, references);
        }
    }

    private static void ReadPython(int number, string code, List<SourceReference> references)
    {
        var from = PythonFrom().Match(code);
        if (from.Success)
        {
            var module = from.Groups[1].Value;
            references.Add(new SourceReference(number, module, "import"));
            foreach (var name in ImportedNames(from.Groups[2].Value))
            {
                if (name != "*")
                {
                    references.Add(new SourceReference(number, module.EndsWith('.') ? module + name : module + "." + name, "import"));
                }
            }

            return;
        }

        var import = PythonImport().Match(code);
        if (import.Success)
        {
            foreach (var name in ImportedNames(import.Groups[1].Value))
            {
                references.Add(new SourceReference(number, name, "import"));
            }
        }
    }

    private static IEnumerable<string> ImportedNames(string list)
    {
        foreach (var part in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = part.Trim('(', ')', '\\', ' ', '\t');
            var alias = name.IndexOf(' ', StringComparison.Ordinal);
            name = alias < 0 ? name : name[..alias];
            if (name.Length > 0)
            {
                yield return name;
            }
        }
    }

    private static void ReadJavaScript(int number, string code, string text, List<SourceReference> references)
    {
        foreach (Match match in JavaScriptFrom().Matches(text))
        {
            AddIfInCode(number, code, text, match, "import", references);
        }

        var bare = JavaScriptBareImport().Match(text);
        if (bare.Success)
        {
            AddIfInCode(number, code, text, bare, "import", references);
        }

        foreach (Match match in JavaScriptCall().Matches(text))
        {
            AddIfInCode(number, code, text, match, "require", references);
        }
    }

    /// <summary>Accepts a match only when its keyword is real code, not text inside a string literal.</summary>
    private static void AddIfInCode(int number, string code, string text, Match match, string form, List<SourceReference> references)
    {
        var index = match.Index;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        if (index >= code.Length || code[index] != text[index])
        {
            return;
        }

        for (var group = 1; group < match.Groups.Count; group++)
        {
            if (match.Groups[group].Success)
            {
                references.Add(new SourceReference(number, match.Groups[group].Value, form));
                return;
            }
        }
    }

    private static bool ReadGo(int number, string code, string text, bool inBlock, bool isFragment, List<SourceReference> references)
    {
        if (inBlock)
        {
            var spec = GoImportSpec().Match(text);
            if (spec.Success)
            {
                references.Add(new SourceReference(number, spec.Groups[1].Value, "import"));
            }

            return !code.Contains(')', StringComparison.Ordinal);
        }

        var single = GoSingleImport().Match(text);
        if (single.Success)
        {
            references.Add(new SourceReference(number, single.Groups[1].Value, "import"));
            return false;
        }

        if (GoImportBlockStart().IsMatch(code))
        {
            return !code.Contains(')', StringComparison.Ordinal);
        }

        if (isFragment)
        {
            // An added line inside an existing import block: a lone (optionally aliased) string literal.
            var lone = GoLoneImportSpec().Match(text);
            if (lone.Success)
            {
                references.Add(new SourceReference(number, lone.Groups[1].Value, "import"));
            }
        }

        return false;
    }

    /// <summary>Dotted identifier chains such as <c>Company.Billing.Invoice</c> used directly in code.</summary>
    private static void AddQualifiedNames(int number, string code, List<SourceReference> references)
    {
        var i = 0;
        while (i < code.Length)
        {
            var c = code[i];
            if (!(char.IsLetter(c) || c == '_') || (i > 0 && (char.IsLetterOrDigit(code[i - 1]) || code[i - 1] is '_' or '.')))
            {
                i++;
                continue;
            }

            var start = i;
            var dots = 0;
            while (i < code.Length)
            {
                if (char.IsLetterOrDigit(code[i]) || code[i] == '_')
                {
                    i++;
                }
                else if (code[i] == '.' && i + 1 < code.Length && (char.IsLetter(code[i + 1]) || code[i + 1] == '_'))
                {
                    dots++;
                    i++;
                }
                else
                {
                    break;
                }
            }

            if (dots > 0)
            {
                references.Add(new SourceReference(number, code[start..i], "qualified name"));
            }
        }
    }

    [GeneratedRegex(@"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:[A-Za-z_]\w*\s*=\s*)?(?:global::)?([A-Za-z_][\w.]*)\s*;", Linear, TimeoutMilliseconds)]
    private static partial Regex CSharpUsing();

    [GeneratedRegex(@"^\s*import\s+(?:static\s+)?([A-Za-z_][\w.]*)", Linear, TimeoutMilliseconds)]
    private static partial Regex JvmImport();

    [GeneratedRegex(@"^\s*from\s+([.\w]+)\s+import\s+(.+)$", Linear, TimeoutMilliseconds)]
    private static partial Regex PythonFrom();

    [GeneratedRegex(@"^\s*import\s+(.+)$", Linear, TimeoutMilliseconds)]
    private static partial Regex PythonImport();

    [GeneratedRegex("""\bfrom\s*(?:"([^"]+)"|'([^']+)')""", Linear, TimeoutMilliseconds)]
    private static partial Regex JavaScriptFrom();

    [GeneratedRegex("""^\s*import\s*(?:"([^"]+)"|'([^']+)')""", Linear, TimeoutMilliseconds)]
    private static partial Regex JavaScriptBareImport();

    [GeneratedRegex("""\b(?:require|import)\s*\(\s*(?:"([^"]+)"|'([^']+)'|`([^`]+)`)\s*\)""", Linear, TimeoutMilliseconds)]
    private static partial Regex JavaScriptCall();

    [GeneratedRegex(@"^\s*import\s+(?:[\w.]+\s+)?""([^""]+)""", Linear, TimeoutMilliseconds)]
    private static partial Regex GoSingleImport();

    [GeneratedRegex(@"^\s*import\s*\(", Linear, TimeoutMilliseconds)]
    private static partial Regex GoImportBlockStart();

    [GeneratedRegex(@"^\s*(?:[\w.]+\s+)?""([^""]+)""", Linear, TimeoutMilliseconds)]
    private static partial Regex GoImportSpec();

    [GeneratedRegex("""^\s*(?:[\w.]+\s+)?"([^"\s]+)"\s*$""", Linear, TimeoutMilliseconds)]
    private static partial Regex GoLoneImportSpec();

    [GeneratedRegex(@"<ProjectReference\b[^>]*\bInclude\s*=\s*""([^""]+)""", Linear, TimeoutMilliseconds)]
    private static partial Regex MSBuildProjectReference();
}

/// <summary>
/// Line-by-line lexer that separates code from comments and string literals. It keeps just enough
/// state (block comments, multi-line strings) to carry across lines.
/// </summary>
internal sealed class SourceSanitizer(SourceLanguage language)
{
    private readonly bool _cLike = language is SourceLanguage.CSharp or SourceLanguage.JavaScript or SourceLanguage.Java
        or SourceLanguage.Kotlin or SourceLanguage.Go;

    private string? _blockCommentEnd;
    private string? _stringEnd;
    private bool _stringEscapes;
    private bool _stringDoubles;

    /// <summary>
    /// Returns the line twice: <c>Code</c> has comments and string contents blanked; <c>Text</c> has
    /// only comments blanked. Both keep the original length so positions line up.
    /// </summary>
    public (string Code, string Text) Next(string line)
    {
        var code = line.ToCharArray();
        var text = line.ToCharArray();
        var i = 0;
        while (i < line.Length)
        {
            if (_blockCommentEnd is not null)
            {
                var end = line.IndexOf(_blockCommentEnd, i, StringComparison.Ordinal);
                var stop = end < 0 ? line.Length : end + _blockCommentEnd.Length;
                Blank(code, i, stop);
                Blank(text, i, stop);
                _blockCommentEnd = end < 0 ? _blockCommentEnd : null;
                i = stop;
            }
            else if (_stringEnd is not null)
            {
                var end = FindStringEnd(line, i);
                var stop = end < 0 ? line.Length : end;
                Blank(code, i, stop);
                if (end >= 0)
                {
                    i = end + _stringEnd.Length;
                    _stringEnd = null;
                }
                else
                {
                    i = line.Length;
                }
            }
            else
            {
                i = ReadCode(line, i, code, text);
            }
        }

        return (new string(code), new string(text));
    }

    private int ReadCode(string line, int i, char[] code, char[] text)
    {
        var c = line[i];
        var rest = line.AsSpan(i);

        if (language == SourceLanguage.MSBuild)
        {
            if (!rest.StartsWith("<!--", StringComparison.Ordinal))
            {
                return i + 1;
            }

            _blockCommentEnd = "-->";
            Blank(code, i, i + 4);
            Blank(text, i, i + 4);
            return i + 4;
        }

        if ((_cLike && rest.StartsWith("//", StringComparison.Ordinal)) || (language == SourceLanguage.Python && c == '#'))
        {
            Blank(code, i, line.Length);
            Blank(text, i, line.Length);
            return line.Length;
        }

        if (_cLike && rest.StartsWith("/*", StringComparison.Ordinal))
        {
            _blockCommentEnd = "*/";
            Blank(code, i, i + 2);
            Blank(text, i, i + 2);
            return i + 2;
        }

        if (c == '"')
        {
            if (language is not (SourceLanguage.JavaScript or SourceLanguage.Go) && rest.StartsWith("\"\"\"", StringComparison.Ordinal))
            {
                return OpenString("\"\"\"", i + 3, escapes: language == SourceLanguage.Python, doubles: false);
            }

            if (language == SourceLanguage.CSharp && i > 0 && (line[i - 1] == '@' || (line[i - 1] == '$' && i > 1 && line[i - 2] == '@')))
            {
                return OpenString("\"", i + 1, escapes: false, doubles: true);
            }

            return SkipSingleLineString(line, i, code);
        }

        if (c == '\'')
        {
            if (language == SourceLanguage.Python && rest.StartsWith("'''", StringComparison.Ordinal))
            {
                return OpenString("'''", i + 3, escapes: true, doubles: false);
            }

            return SkipSingleLineString(line, i, code);
        }

        if (c == '`' && language is SourceLanguage.JavaScript or SourceLanguage.Go)
        {
            return OpenString("`", i + 1, escapes: language == SourceLanguage.JavaScript, doubles: false);
        }

        return i + 1;
    }

    private int OpenString(string terminator, int next, bool escapes, bool doubles)
    {
        _stringEnd = terminator;
        _stringEscapes = escapes;
        _stringDoubles = doubles;
        return next;
    }

    private static int SkipSingleLineString(string line, int open, char[] code)
    {
        var quote = line[open];
        var i = open + 1;
        while (i < line.Length && line[i] != quote)
        {
            i += line[i] == '\\' ? 2 : 1;
        }

        var close = Math.Min(i, line.Length);
        Blank(code, open + 1, close);
        return Math.Min(close + 1, line.Length);
    }

    private int FindStringEnd(string line, int start)
    {
        var terminator = _stringEnd!;
        var i = start;
        while (i < line.Length)
        {
            if (_stringEscapes && line[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (string.CompareOrdinal(line, i, terminator, 0, terminator.Length) == 0)
            {
                if (_stringDoubles && i + 1 < line.Length && line[i + 1] == '"')
                {
                    i += 2;
                    continue;
                }

                return i;
            }

            i++;
        }

        return -1;
    }

    private static void Blank(char[] buffer, int start, int end)
    {
        for (var i = start; i < end && i < buffer.Length; i++)
        {
            buffer[i] = ' ';
        }
    }
}

/// <summary>
/// A namespace/module pattern. <c>*</c> matches any run of characters. A pattern that does not end
/// in <c>*</c> also matches everything nested below it, so <c>Company.Billing</c> covers
/// <c>Company.Billing.Invoices</c> and <c>lodash</c> covers <c>lodash/fp</c>. Matching is case-sensitive.
/// </summary>
internal sealed class ModulePattern
{
    private ModulePattern(string value) => Value = value;

    public string Value { get; }

    public static ModulePattern Parse(string pattern) => new(pattern.Trim());

    public bool IsMatch(string reference)
    {
        if (Wildcard.IsMatch(Value, reference))
        {
            return true;
        }

        if (Value.EndsWith('*'))
        {
            return false;
        }

        for (var i = 1; i < reference.Length; i++)
        {
            if (reference[i] is '.' or '/' or ':' or '\\' && Wildcard.IsMatch(Value, reference.AsSpan(0, i)))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Linear-space wildcard matching where <c>*</c> matches any run of characters.</summary>
internal static class Wildcard
{
    public static bool IsMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text, bool ignoreCase = false)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (p < pattern.Length && Same(pattern[p], text[t], ignoreCase))
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static bool Same(char a, char b, bool ignoreCase) =>
        a == b || (ignoreCase && char.ToUpperInvariant(a) == char.ToUpperInvariant(b));
}
