using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Axiom.Domain.Policy.Custom;

public enum CustomCheckType
{
    ForbidRegex,
    RequireRegex,
    ForbidPath,
    RequireFile,
    JsonPath,
}

public enum JsonPathOperator
{
    Exists,
    Absent,
    Equal,
    NotEqual,
    Matches,
}

/// <summary>One check of a custom rule. String fields may contain <c>{{parameter}}</c> placeholders.</summary>
public sealed record CustomCheck(
    CustomCheckType Type,
    string? Message,
    string? Pattern,
    bool AddedLinesOnly,
    string? Path,
    JsonPathOperator Operator,
    string? Value);

/// <summary>Which changed files a rule looks at.</summary>
public sealed record CustomSelection(
    ImmutableArray<string> Paths,
    ImmutableArray<string> Exclude,
    ImmutableArray<ChangeKind> Kinds);

/// <summary>
/// A parsed declarative custom rule (see docs/custom-policy-runner.md). Parsing is strict: unknown
/// properties, unknown check types and oversized definitions are errors, never ignored.
/// </summary>
public sealed class CustomRuleSpec
{
    public const string SchemaId = "axiom-custom-rule/v1";
    public const int MaxSpecCharacters = 64 * 1024;
    public const int MaxChecks = 20;
    public const int MaxPatternCharacters = 1_000;
    public const int MaxParameters = 20;

    private static readonly string[] KindNames = ["added", "modified", "deleted", "renamed"];

    private CustomRuleSpec(
        ImmutableSortedDictionary<string, string?> parameters,
        CustomSelection selection,
        ImmutableArray<CustomCheck> checks)
    {
        Parameters = parameters;
        Selection = selection;
        Checks = checks;
    }

    /// <summary>Declared parameters. A null default marks the parameter as required.</summary>
    public ImmutableSortedDictionary<string, string?> Parameters { get; }

    public CustomSelection Selection { get; }

    public ImmutableArray<CustomCheck> Checks { get; }

    public static CustomRuleSpec Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxSpecCharacters)
        {
            throw new CustomRuleException($"The rule definition exceeds {MaxSpecCharacters} characters.");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            return ParseRoot(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new CustomRuleException("The rule definition is not valid JSON.", ex);
        }
    }

    /// <summary>The spec with every <c>{{parameter}}</c> replaced. Missing required or undeclared parameters are errors.</summary>
    public CustomRuleSpec Bind(IReadOnlyDictionary<string, string> supplied)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        var unknown = supplied.Keys.Where(k => !Parameters.ContainsKey(k)).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            throw new CustomRuleException(CustomRuleCodes.InvalidParameters, $"Unknown parameter(s): {string.Join(", ", unknown)}.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, fallback) in Parameters)
        {
            if (supplied.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
            {
                values[name] = value;
            }
            else if (fallback is not null)
            {
                values[name] = fallback;
            }
            else
            {
                throw new CustomRuleException(CustomRuleCodes.InvalidParameters, $"Required parameter '{name}' is missing.");
            }
        }

        string? Sub(string? text) => text is null ? null : Substitute(text, values);
        ImmutableArray<string> SubAll(ImmutableArray<string> items) => [.. items.Select(i => Substitute(i, values))];

        return new CustomRuleSpec(
            Parameters,
            Selection with { Paths = SubAll(Selection.Paths), Exclude = SubAll(Selection.Exclude) },
            [.. Checks.Select(c => c with { Message = Sub(c.Message), Pattern = Sub(c.Pattern), Path = Sub(c.Path), Value = Sub(c.Value) })]);
    }

    internal static Regex CompileRegex(string pattern, TimeSpan timeout)
    {
        try
        {
            // NonBacktracking guarantees linear-time matching (no catastrophic backtracking) and rejects
            // lookarounds and backreferences, which the language deliberately does not offer.
            return new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, timeout);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new CustomRuleException("A pattern is not a supported regular expression (lookarounds and backreferences are not available).", ex);
        }
    }

    private static string Substitute(string text, Dictionary<string, string> values)
    {
        if (!text.Contains("{{", StringComparison.Ordinal))
        {
            return text;
        }

        // Single pass over the template: substituted values are never re-scanned.
        var result = new StringBuilder();
        var index = 0;
        while (index < text.Length)
        {
            var open = text.IndexOf("{{", index, StringComparison.Ordinal);
            var close = open < 0 ? -1 : text.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                result.Append(text, index, text.Length - index);
                break;
            }

            result.Append(text, index, open - index);
            var name = text.AsSpan(open + 2, close - open - 2).Trim().ToString();
            if (!values.TryGetValue(name, out var value))
            {
                throw new CustomRuleException($"The rule refers to undeclared parameter '{name}'.");
            }

            result.Append(value);
            index = close + 2;
        }

        return result.ToString();
    }

    private static CustomRuleSpec ParseRoot(JsonElement root)
    {
        RequireObject(root, "rule");
        Allow(root, "rule", "schema", "description", "parameters", "select", "checks");

        if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String || schema.GetString() != SchemaId)
        {
            throw new CustomRuleException($"'schema' must be \"{SchemaId}\".");
        }

        var parameters = ImmutableSortedDictionary.CreateBuilder<string, string?>(StringComparer.Ordinal);
        if (root.TryGetProperty("parameters", out var declared))
        {
            RequireObject(declared, "parameters");
            foreach (var parameter in declared.EnumerateObject())
            {
                if (parameters.Count >= MaxParameters || parameter.Name.Length is 0 or > 64)
                {
                    throw new CustomRuleException("Too many parameters, or a parameter name has an invalid length.");
                }

                parameters[parameter.Name] = parameter.Value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.String => parameter.Value.GetString(),
                    _ => throw new CustomRuleException($"The default of parameter '{parameter.Name}' must be a string or null."),
                };
            }
        }

        var selection = ParseSelection(root);

        if (!root.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array
            || checks.GetArrayLength() is 0 or > MaxChecks)
        {
            throw new CustomRuleException($"'checks' must be an array of 1 to {MaxChecks} checks.");
        }

        return new CustomRuleSpec(parameters.ToImmutable(), selection, [.. checks.EnumerateArray().Select(ParseCheck)]);
    }

    private static CustomSelection ParseSelection(JsonElement root)
    {
        if (!root.TryGetProperty("select", out var select))
        {
            return new CustomSelection(["**"], [], [.. Enum.GetValues<ChangeKind>()]);
        }

        RequireObject(select, "select");
        Allow(select, "select", "paths", "exclude", "kinds");
        var paths = StringList(select, "paths");
        var kinds = StringList(select, "kinds");
        foreach (var kind in kinds.Where(k => !KindNames.Contains(k, StringComparer.Ordinal)))
        {
            throw new CustomRuleException($"Unknown change kind '{kind}'. Use added, modified, deleted or renamed.");
        }

        return new CustomSelection(
            paths.IsEmpty ? ["**"] : paths,
            StringList(select, "exclude"),
            kinds.IsEmpty
                ? [.. Enum.GetValues<ChangeKind>()]
                : [.. kinds.Select(k => (ChangeKind)Array.IndexOf(KindNames, k))]);
    }

    private static CustomCheck ParseCheck(JsonElement element)
    {
        RequireObject(element, "check");
        if (!element.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            throw new CustomRuleException("Every check needs a string 'type'.");
        }

        var message = OptionalString(element, "message");
        switch (typeElement.GetString())
        {
            case "forbid-regex":
                Allow(element, "check", "type", "message", "pattern", "scope");
                var scope = OptionalString(element, "scope") ?? "added-lines";
                if (scope is not ("added-lines" or "content"))
                {
                    throw new CustomRuleException("'scope' must be \"added-lines\" or \"content\".");
                }

                return new CustomCheck(CustomCheckType.ForbidRegex, message, Pattern(element), scope == "added-lines", null, default, null);
            case "require-regex":
                Allow(element, "check", "type", "message", "pattern");
                return new CustomCheck(CustomCheckType.RequireRegex, message, Pattern(element), false, null, default, null);
            case "forbid-path":
                Allow(element, "check", "type", "message");
                return new CustomCheck(CustomCheckType.ForbidPath, message, null, false, null, default, null);
            case "require-file":
                Allow(element, "check", "type", "message", "path");
                return new CustomCheck(CustomCheckType.RequireFile, message, null, false, RequiredString(element, "path"), default, null);
            case "json-path":
                Allow(element, "check", "type", "message", "path", "op", "value");
                var op = RequiredString(element, "op") switch
                {
                    "exists" => JsonPathOperator.Exists,
                    "absent" => JsonPathOperator.Absent,
                    "equals" => JsonPathOperator.Equal,
                    "not-equals" => JsonPathOperator.NotEqual,
                    "matches" => JsonPathOperator.Matches,
                    _ => throw new CustomRuleException("'op' must be exists, absent, equals, not-equals or matches."),
                };
                var value = OptionalString(element, "value");
                if (op is not (JsonPathOperator.Exists or JsonPathOperator.Absent) && value is null)
                {
                    throw new CustomRuleException("This json-path operator needs a 'value'.");
                }

                var path = RequiredString(element, "path");
                if (!path.Contains("{{", StringComparison.Ordinal))
                {
                    _ = JsonPathExpression.Parse(path);
                }

                return new CustomCheck(CustomCheckType.JsonPath, message, null, false, path, op, value);
            default:
                throw new CustomRuleException($"Unknown check type '{typeElement.GetString()}'.");
        }
    }

    private static string Pattern(JsonElement element)
    {
        var pattern = RequiredString(element, "pattern");
        if (pattern.Length > MaxPatternCharacters)
        {
            throw new CustomRuleException($"A pattern is longer than {MaxPatternCharacters} characters.");
        }

        return pattern;
    }

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) is { Length: > 0 } value
            ? value
            : throw new CustomRuleException($"'{name}' is required and must be a non-empty string.");

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String && property.GetString()!.Length <= 1_000
            ? property.GetString()
            : throw new CustomRuleException($"'{name}' must be a string of at most 1000 characters.");
    }

    private static ImmutableArray<string> StringList(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var list))
        {
            return [];
        }

        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 50
            || list.EnumerateArray().Any(i => i.ValueKind != JsonValueKind.String || i.GetString()!.Length is 0 or > 500))
        {
            throw new CustomRuleException($"'{name}' must be an array of at most 50 non-empty strings.");
        }

        return [.. list.EnumerateArray().Select(i => i.GetString()!)];
    }

    private static void RequireObject(JsonElement element, string what)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new CustomRuleException($"The {what} must be a JSON object.");
        }
    }

    private static void Allow(JsonElement element, string what, params string[] allowed)
    {
        var unknown = element.EnumerateObject().Select(p => p.Name).Where(n => !allowed.Contains(n, StringComparer.Ordinal)).ToArray();
        if (unknown.Length > 0)
        {
            throw new CustomRuleException($"The {what} has unknown property '{unknown[0]}'.");
        }
    }
}
