using System.Collections.Immutable;
using System.Globalization;
using Axiom.Domain.Governance;

namespace Axiom.Domain.Policy.Rules.Support;

/// <summary>
/// Validated access to a rule binding's <c>with</c> parameters. Unknown names, missing required
/// values and malformed values all raise <see cref="PolicyParameterException"/>, so a misconfigured
/// binding is reported as an error and never evaluated as a pass.
/// </summary>
internal sealed class RuleParameters
{
    private static readonly char[] ListSeparators = ['\n', '\r', ','];
    private static readonly char[] LineSeparators = ['\n', '\r'];
    private readonly IPolicyRule _rule;
    private readonly IReadOnlyDictionary<string, string> _values;

    private RuleParameters(IPolicyRule rule, IReadOnlyDictionary<string, string> values)
    {
        _rule = rule;
        _values = values;
    }

    public static RuleParameters Bind(IPolicyRule rule, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var unknown = parameters.Keys.Where(k => !rule.Parameters.ContainsKey(k)).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            throw new PolicyParameterException(
                $"Rule '{rule.RuleId}' does not accept parameter(s) [{string.Join(", ", unknown)}]. "
                + $"Accepted: [{string.Join(", ", rule.Parameters.Keys.Order(StringComparer.Ordinal))}].");
        }

        return new RuleParameters(rule, parameters);
    }

    public bool Has(string name) => _values.TryGetValue(name, out var raw) && !string.IsNullOrWhiteSpace(raw);

    /// <summary>A newline- or comma-separated list; empty when the parameter is absent.</summary>
    public ImmutableArray<string> List(string name) => Split(name, ListSeparators);

    public ImmutableArray<string> RequiredList(string name)
    {
        var values = List(name);
        return values.IsEmpty ? throw Missing(name) : values;
    }

    /// <summary>A newline-separated list, for values that may themselves contain commas.</summary>
    public ImmutableArray<string> Lines(string name) => Split(name, LineSeparators);

    public ImmutableArray<GlobPattern> Globs(string name) => [.. List(name).Select(v => ParseGlob(name, v))];

    public ImmutableArray<GlobPattern> RequiredGlobs(string name) => [.. RequiredList(name).Select(v => ParseGlob(name, v))];

    public bool Flag(string name, bool defaultValue)
    {
        if (!Has(name))
        {
            return defaultValue;
        }

        var value = _values[name].Trim();
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            ? false
            : throw Malformed(name, "expected 'true' or 'false'");
    }

    /// <summary>One of a closed set of case-insensitive options; returns the canonical spelling.</summary>
    public string Choice(string name, string defaultValue, params string[] options)
    {
        if (!Has(name))
        {
            return defaultValue;
        }

        var value = _values[name].Trim();
        return options.FirstOrDefault(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase))
            ?? throw Malformed(name, $"expected one of [{string.Join(", ", options)}]");
    }

    /// <summary>A list restricted to a closed set of case-insensitive options; all options when absent.</summary>
    public ImmutableArray<string> Choices(string name, IReadOnlyCollection<string> options)
    {
        if (!Has(name))
        {
            return [.. options];
        }

        return
        [
            .. List(name).Select(value =>
                options.FirstOrDefault(o => string.Equals(o, value, StringComparison.OrdinalIgnoreCase))
                ?? throw Malformed(name, $"'{value}' is not one of [{string.Join(", ", options)}]")),
        ];
    }

    public int Integer(string name, int defaultValue, int minimum, int maximum)
    {
        if (!Has(name))
        {
            return defaultValue;
        }

        return int.TryParse(_values[name].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value >= minimum && value <= maximum
            ? value
            : throw Malformed(name, $"expected an integer between {minimum} and {maximum}");
    }

    public PolicyParameterException Malformed(string name, string problem) =>
        new($"Rule '{_rule.RuleId}' parameter '{name}' is malformed: {problem}.");

    private PolicyParameterException Missing(string name) =>
        new($"Rule '{_rule.RuleId}' requires parameter '{name}'.");

    private ImmutableArray<string> Split(string name, char[] separators) =>
        _values.TryGetValue(name, out var raw) && raw is not null
            ? [.. raw.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : [];

    private GlobPattern ParseGlob(string name, string value)
    {
        try
        {
            return GlobPattern.Parse(value);
        }
        catch (ArgumentException ex)
        {
            throw new PolicyParameterException($"Rule '{_rule.RuleId}' parameter '{name}' contains an invalid glob '{value}'.", ex);
        }
    }
}

/// <summary>Helpers shared by rules for selecting changed files and the lines a change introduced.</summary>
internal static class ChangeSet
{
    public static bool MatchesAny(this ImmutableArray<GlobPattern> globs, string path)
    {
        foreach (var glob in globs)
        {
            if (glob.IsMatch(path))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Non-deleted changed files inside <paramref name="include"/> (all when empty) and outside <paramref name="exclude"/>.</summary>
    public static IEnumerable<ChangedFile> Present(PolicyContext context, ImmutableArray<GlobPattern> include, ImmutableArray<GlobPattern> exclude) =>
        context.Changes.Where(c => c.Kind != ChangeKind.Deleted
            && (include.IsEmpty || include.MatchesAny(c.Path))
            && !exclude.MatchesAny(c.Path));

    /// <summary>True when the whole file is new at its path, so all of its content is attributable to the change.</summary>
    public static bool IsWhollyNew(ChangedFile file) => file.Kind is ChangeKind.Added or ChangeKind.Renamed;

    public static string[] SplitLines(string content) => content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    public static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    public static string Directory(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    /// <summary>
    /// The lines this change introduced: the reported added lines, or every line of a file that is
    /// new at its path when the caller supplied content without a line-level diff.
    /// </summary>
    public static IEnumerable<AddedLine> NewLines(ChangedFile file)
    {
        if (!file.AddedLines.IsDefaultOrEmpty)
        {
            return file.AddedLines;
        }

        return IsWhollyNew(file) && file.Content is not null
            ? SplitLines(file.Content).Select((text, index) => new AddedLine(index + 1, text))
            : [];
    }

    /// <summary>
    /// Whether a finding at a given line was introduced by the change: any line of an added or renamed
    /// file, but only the added lines of a modified file, so untouched legacy lines are not re-reported.
    /// </summary>
    public static Func<int, bool> IntroducedLines(ChangedFile file)
    {
        if (IsWhollyNew(file))
        {
            return _ => true;
        }

        if (file.AddedLines.IsDefaultOrEmpty)
        {
            return _ => false;
        }

        var added = file.AddedLines.Select(l => l.Number).ToHashSet();
        return added.Contains;
    }
}
