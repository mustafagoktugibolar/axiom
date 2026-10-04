using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Axiom.Domain.Policy.Rules.Support;

/// <summary>One package dependency declared in a manifest. <see cref="Version"/> is null when the manifest states none.</summary>
internal sealed record PackageDeclaration(string Ecosystem, string Id, string? Version, int Line);

/// <summary>
/// Reads dependency declarations from package manifests: NuGet (<c>*.csproj</c>, <c>*.fsproj</c>,
/// <c>*.vbproj</c>, <c>Directory.Packages.props</c>, <c>Directory.Build.props</c>), npm
/// (<c>package.json</c>), Python (<c>requirements*.txt</c>, <c>constraints*.txt</c>,
/// <c>pyproject.toml</c>), Go (<c>go.mod</c>) and Maven/Gradle (<c>pom.xml</c>, <c>build.gradle</c>,
/// <c>build.gradle.kts</c>). Malformed manifests raise <see cref="FormatException"/>.
/// </summary>
internal static partial class PackageManifests
{
    public const string NuGet = "nuget";
    public const string Npm = "npm";
    public const string PyPi = "pypi";
    public const string Go = "go";
    public const string Maven = "maven";

    private const RegexOptions Linear = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private const int TimeoutMilliseconds = 5000;
    private static readonly string[] NpmSections = ["dependencies", "devDependencies", "peerDependencies", "optionalDependencies"];

    public static bool IsManifest(string path) => Kind(path) is not null;

    public static IReadOnlyList<PackageDeclaration> Read(string path, string content) => Kind(path) switch
    {
        NuGet => ReadMSBuild(content),
        Npm => ReadPackageJson(content),
        "requirements" => ReadRequirements(content),
        "pyproject" => ReadPyProject(content),
        Go => ReadGoMod(content),
        "pom" => ReadPom(content),
        "gradle" => ReadGradle(content),
        _ => [],
    };

    /// <summary>Canonical PyPI name: lower-case with runs of <c>-</c>, <c>_</c> and <c>.</c> collapsed to <c>-</c>.</summary>
    public static string NormalizePythonName(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is '-' or '_' or '.')
            {
                if (builder.Length == 0 || builder[^1] != '-')
                {
                    builder.Append('-');
                }
            }
            else
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static string? Kind(string path)
    {
        var name = ChangeSet.FileName(path);
        if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase))
        {
            return NuGet;
        }

        if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            && (name.StartsWith("requirements", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("constraints", StringComparison.OrdinalIgnoreCase)))
        {
            return "requirements";
        }

        return name switch
        {
            "package.json" => Npm,
            "pyproject.toml" => "pyproject",
            "go.mod" => Go,
            "pom.xml" => "pom",
            "build.gradle" or "build.gradle.kts" => "gradle",
            _ => null,
        };
    }

    private static XDocument LoadXml(string content)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = PolicyLimits.MaxFileCharacters,
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(content), settings);
            return XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            throw new FormatException($"The XML manifest is malformed near line {ex.LineNumber}.", ex);
        }
    }

    private static int LineOf(XObject node) => ((IXmlLineInfo)node).HasLineInfo() ? ((IXmlLineInfo)node).LineNumber : 1;

    private static string? Child(XElement element, string localName) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value.Trim();

    private static List<PackageDeclaration> ReadMSBuild(string content)
    {
        var declarations = new List<PackageDeclaration>();
        foreach (var element in LoadXml(content).Descendants())
        {
            if (element.Name.LocalName is not ("PackageReference" or "PackageVersion" or "GlobalPackageReference"))
            {
                continue;
            }

            var id = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var version = element.Attribute("Version")?.Value
                ?? element.Attribute("VersionOverride")?.Value
                ?? Child(element, "Version");
            declarations.Add(new PackageDeclaration(NuGet, id.Trim(), NullIfBlank(version), LineOf(element)));
        }

        return declarations;
    }

    private static List<PackageDeclaration> ReadPom(string content)
    {
        var declarations = new List<PackageDeclaration>();
        foreach (var element in LoadXml(content).Descendants())
        {
            if (element.Name.LocalName is not ("dependency" or "plugin" or "parent"))
            {
                continue;
            }

            var artifact = Child(element, "artifactId");
            if (string.IsNullOrEmpty(artifact))
            {
                continue;
            }

            var group = Child(element, "groupId");
            var id = string.IsNullOrEmpty(group) ? artifact : group + ":" + artifact;
            declarations.Add(new PackageDeclaration(Maven, id, NullIfBlank(Child(element, "version")), LineOf(element)));
        }

        return declarations;
    }

    private static List<PackageDeclaration> ReadPackageJson(string content)
    {
        var declarations = new List<PackageDeclaration>();
        if (StructuredDocument.ParseJson(content) is not DocMap root)
        {
            throw new FormatException("package.json must contain a JSON object.");
        }

        foreach (var section in NpmSections)
        {
            foreach (var entry in root.Map(section)?.Entries ?? [])
            {
                declarations.Add(new PackageDeclaration(Npm, entry.Key, NullIfBlank((entry.Value as DocScalar)?.Text), entry.Line));
            }
        }

        return declarations;
    }

    private static List<PackageDeclaration> ReadRequirements(string content)
    {
        var declarations = new List<PackageDeclaration>();
        var lines = ChangeSet.SplitLines(content);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var comment = line.IndexOf(" #", StringComparison.Ordinal);
            line = (comment >= 0 ? line[..comment] : line).Trim();
            if (line.Length == 0 || line[0] is '#' or '-')
            {
                continue;
            }

            if (TryReadRequirement(line, i + 1, out var declaration))
            {
                declarations.Add(declaration);
            }
        }

        return declarations;
    }

    /// <summary>Reads a PEP 508 requirement such as <c>requests[security]&gt;=2.31,&lt;3 ; python_version &gt; "3.8"</c>.</summary>
    private static bool TryReadRequirement(string requirement, int line, out PackageDeclaration declaration)
    {
        declaration = null!;
        var match = PythonRequirement().Match(requirement);
        if (!match.Success)
        {
            return false;
        }

        string? pinned = null, lower = null;
        foreach (var clause in match.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var spec = clause.Trim('(', ')', ' ');
            if (spec.StartsWith("==", StringComparison.Ordinal))
            {
                pinned ??= spec.TrimStart('=').Trim();
            }
            else if (spec.StartsWith(">=", StringComparison.Ordinal) || spec.StartsWith("~=", StringComparison.Ordinal))
            {
                lower ??= spec[2..].Trim();
            }
            else if (spec.StartsWith('>'))
            {
                lower ??= spec[1..].Trim();
            }
        }

        declaration = new PackageDeclaration(PyPi, match.Groups[1].Value, NullIfBlank(pinned ?? lower), line);
        return true;
    }

    /// <summary>
    /// Reads the dependency tables of a pyproject.toml: PEP 621 (<c>[project] dependencies</c>,
    /// <c>[project.optional-dependencies]</c>), PEP 735 (<c>[dependency-groups]</c>) and Poetry tables.
    /// </summary>
    private static List<PackageDeclaration> ReadPyProject(string content)
    {
        var declarations = new List<PackageDeclaration>();
        var lines = ChangeSet.SplitLines(content);
        var table = string.Empty;
        var inArray = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (inArray)
            {
                AddQuotedRequirements(line, i + 1, declarations);
                inArray = !line.Contains(']', StringComparison.Ordinal);
                continue;
            }

            if (line[0] == '[')
            {
                table = line.Trim('[', ']', ' ').Replace("\"", string.Empty, StringComparison.Ordinal);
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals].Trim().Trim('"', '\'');
            var value = line[(equals + 1)..].Trim();
            var isPoetryTable = table.StartsWith("tool.poetry.", StringComparison.Ordinal)
                && (table.EndsWith(".dependencies", StringComparison.Ordinal) || table.EndsWith(".dev-dependencies", StringComparison.Ordinal));

            if (isPoetryTable)
            {
                if (key != "python")
                {
                    var version = TomlVersion().Match(value);
                    declarations.Add(new PackageDeclaration(
                        PyPi, key, version.Success ? NullIfBlank(version.Groups[1].Value.TrimStart('^', '~', '=', '>', ' ')) : null, i + 1));
                }
            }
            else if ((table == "project" && key == "dependencies")
                || table is "project.optional-dependencies" or "dependency-groups")
            {
                AddQuotedRequirements(value, i + 1, declarations);
                inArray = value.StartsWith('[') && !value.Contains(']', StringComparison.Ordinal);
            }
        }

        return declarations;
    }

    private static void AddQuotedRequirements(string text, int line, List<PackageDeclaration> declarations)
    {
        foreach (Match match in QuotedText().Matches(text))
        {
            var requirement = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (TryReadRequirement(requirement, line, out var declaration))
            {
                declarations.Add(declaration);
            }
        }
    }

    private static List<PackageDeclaration> ReadGoMod(string content)
    {
        var declarations = new List<PackageDeclaration>();
        var lines = ChangeSet.SplitLines(content);
        var inRequire = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var comment = line.IndexOf("//", StringComparison.Ordinal);
            line = (comment >= 0 ? line[..comment] : line).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (inRequire)
            {
                if (line[0] == ')')
                {
                    inRequire = false;
                    continue;
                }
            }
            else if (line.StartsWith("require", StringComparison.Ordinal))
            {
                line = line["require".Length..].Trim();
                if (line.StartsWith('('))
                {
                    inRequire = true;
                    continue;
                }
            }
            else
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1)
            {
                declarations.Add(new PackageDeclaration(Go, parts[0].Trim('"'), parts.Length > 1 ? parts[1] : null, i + 1));
            }
        }

        return declarations;
    }

    private static List<PackageDeclaration> ReadGradle(string content)
    {
        var declarations = new List<PackageDeclaration>();
        var sanitizer = new SourceSanitizer(SourceLanguage.Kotlin);
        var lines = ChangeSet.SplitLines(content);
        for (var i = 0; i < lines.Length; i++)
        {
            var text = sanitizer.Next(lines[i]).Text;
            foreach (Match match in GradleCoordinate().Matches(text))
            {
                declarations.Add(new PackageDeclaration(
                    Maven, match.Groups[1].Value + ":" + match.Groups[2].Value, NullIfBlank(match.Groups[3].Value), i + 1));
            }

            foreach (Match match in GradleMapNotation().Matches(text))
            {
                declarations.Add(new PackageDeclaration(
                    Maven, match.Groups[1].Value + ":" + match.Groups[2].Value, NullIfBlank(match.Groups[3].Value), i + 1));
            }
        }

        return declarations;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^\s*([A-Za-z0-9][A-Za-z0-9._\-]*)\s*(?:\[[^\]]*\])?\s*([^;#]*)", Linear, TimeoutMilliseconds)]
    private static partial Regex PythonRequirement();

    [GeneratedRegex("""(?:^|version\s*=\s*)["']([^"']*)["']""", Linear, TimeoutMilliseconds)]
    private static partial Regex TomlVersion();

    [GeneratedRegex("\"([^\"]*)\"|'([^']*)'", Linear, TimeoutMilliseconds)]
    private static partial Regex QuotedText();

    [GeneratedRegex("""["']([A-Za-z0-9_.\-]+):([A-Za-z0-9_.\-]+)(?::([^"'@:]+))?(?:@[A-Za-z0-9]+)?["']""", Linear, TimeoutMilliseconds)]
    private static partial Regex GradleCoordinate();

    [GeneratedRegex("""group\s*[:=]\s*["']([^"']+)["']\s*,\s*name\s*[:=]\s*["']([^"']+)["'](?:\s*,\s*version\s*[:=]\s*["']([^"']+)["'])?""", Linear, TimeoutMilliseconds)]
    private static partial Regex GradleMapNotation();
}

/// <summary>A dotted numeric version with an optional pre-release marker, comparable across ecosystems.</summary>
internal sealed class PackageVersion : IComparable<PackageVersion>
{
    private static readonly string[] PreReleaseMarkers = ["alpha", "beta", "rc", "snapshot", "preview", "dev", "pre", "canary", "nightly"];
    private readonly long[] _parts;
    private readonly bool _preRelease;

    private PackageVersion(long[] parts, bool preRelease)
    {
        _parts = parts;
        _preRelease = preRelease;
    }

    /// <summary>Parses a concrete version, tolerating a leading <c>v</c> or range operator (<c>^1.2.3</c> reads as 1.2.3).</summary>
    public static bool TryParse(string? text, out PackageVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim().TrimStart('^', '~', '=', '>', '<', ' ', '[', '(').TrimStart('v', 'V');
        var end = value.IndexOfAny([' ', ',', ']', ')', '|']);
        value = end >= 0 ? value[..end] : value;
        var plus = value.IndexOf('+', StringComparison.Ordinal);
        value = plus >= 0 ? value[..plus] : value;

        var parts = new List<long>();
        var position = 0;
        while (position < value.Length)
        {
            var start = position;
            while (position < value.Length && char.IsAsciiDigit(value[position]))
            {
                position++;
            }

            if (position == start || position - start > 18)
            {
                break;
            }

            parts.Add(long.Parse(value.AsSpan(start, position - start), System.Globalization.CultureInfo.InvariantCulture));
            if (position < value.Length && value[position] == '.' && position + 1 < value.Length && char.IsAsciiDigit(value[position + 1]))
            {
                position++;
                continue;
            }

            break;
        }

        if (parts.Count == 0)
        {
            return false;
        }

        var qualifier = value[position..];
        if (qualifier.Length > 0 && qualifier[0] is not ('-' or '.' or '_') && !char.IsAsciiLetter(qualifier[0]))
        {
            return false;
        }

        version = new PackageVersion([.. parts], PreReleaseMarkers.Any(m => qualifier.Contains(m, StringComparison.OrdinalIgnoreCase)));
        return true;
    }

    public int CompareTo(PackageVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (var i = 0; i < Math.Max(_parts.Length, other._parts.Length); i++)
        {
            var order = (i < _parts.Length ? _parts[i] : 0).CompareTo(i < other._parts.Length ? other._parts[i] : 0);
            if (order != 0)
            {
                return order;
            }
        }

        return (_preRelease ? 0 : 1).CompareTo(other._preRelease ? 0 : 1);
    }
}

/// <summary>A conjunction of comparators, e.g. <c>&gt;=1.0 &lt;2.0</c>; a bare version means exactly that version.</summary>
internal sealed class PackageVersionRange
{
    private readonly (string Operator, PackageVersion Version)[] _comparators;

    private PackageVersionRange(string text, (string, PackageVersion)[] comparators)
    {
        Text = text;
        _comparators = comparators;
    }

    public string Text { get; }

    public static bool TryParse(string text, out PackageVersionRange range)
    {
        range = null!;
        var comparators = new List<(string, PackageVersion)>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var length = 0;
            while (length < token.Length && token[length] is '<' or '>' or '=' or '!')
            {
                length++;
            }

            var op = token[..length] switch
            {
                "" or "=" or "==" => "=",
                "<" or "<=" or ">" or ">=" or "!=" => token[..length],
                _ => null,
            };

            var operand = token[length..];
            if (op is null || operand.Length == 0 || !char.IsAsciiDigit(operand.TrimStart('v', 'V').FirstOrDefault())
                || !PackageVersion.TryParse(operand, out var version))
            {
                return false;
            }

            comparators.Add((op, version));
        }

        if (comparators.Count == 0)
        {
            return false;
        }

        range = new PackageVersionRange(text.Trim(), [.. comparators]);
        return true;
    }

    public bool Contains(PackageVersion version) => _comparators.All(c =>
    {
        var order = version.CompareTo(c.Version);
        return c.Operator switch
        {
            "=" => order == 0,
            "!=" => order != 0,
            "<" => order < 0,
            "<=" => order <= 0,
            ">" => order > 0,
            _ => order >= 0,
        };
    });
}
