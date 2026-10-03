using Axiom.Domain.Governance;
using Axiom.Domain.Policy.Rules.Support;

namespace Axiom.Domain.Policy.Rules;

/// <summary>Required repository metadata files such as CODEOWNERS or .axiom/catalog.yaml.</summary>
public sealed class RequiredFileRule : BuiltInRule
{
    public override string RuleId => "required-file";

    public override string Version => "1.0.0";

    public override string Description =>
        "Every entry of 'files' must exist in the repository tree at the evaluated commit, optionally containing required text or keys.";

    public override IReadOnlyDictionary<string, string> Parameters { get; } = Describe(
        ("files", "Required. Paths or globs that must exist. Alternatives for one requirement are joined with '|', "
            + "e.g. CODEOWNERS|.github/CODEOWNERS|docs/CODEOWNERS."),
        ("contains", "Optional. Newline-separated text fragments each required file must contain."),
        ("keys", "Optional. Dotted keys that must have a value in each required file, which is then read as JSON or YAML."));

    private protected override IEnumerable<PolicyViolation> Run(PolicyContext context, RuleParameters parameters)
    {
        var requirements = parameters.RequiredList("files")
            .Select(entry => entry.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();
        if (requirements.Any(r => r.Length == 0))
        {
            throw parameters.Malformed("files", "an entry has no path");
        }

        var globs = requirements.Select(r => r.Select(a => (Alternative: a, Glob: ParseGlob(parameters, a))).ToArray()).ToArray();
        var contains = parameters.Lines("contains");
        var keys = parameters.List("keys");
        var treePaths = new Lazy<string[]>(() => [.. context.Tree.Paths.Order(StringComparer.Ordinal)]);

        foreach (var requirement in globs)
        {
            var found = requirement
                .SelectMany(a => a.Glob.HasWildcards
                    ? treePaths.Value.Where(a.Glob.IsMatch)
                    : context.Tree.Exists(a.Glob.Value) ? [a.Glob.Value] : [])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

            if (found.Length == 0)
            {
                var display = string.Join(" or ", requirement.Select(a => a.Alternative));
                yield return new PolicyViolation(
                    $"Required file is missing: {display}.",
                    requirement[0].Glob.HasWildcards ? null : requirement[0].Glob.Value,
                    Evidence: $"no file in the repository matches {display}");
                continue;
            }

            if (contains.IsEmpty && keys.IsEmpty)
            {
                continue;
            }

            foreach (var path in found)
            {
                foreach (var violation in CheckContent(context, path, contains, keys))
                {
                    yield return violation;
                }
            }
        }
    }

    private static GlobPattern ParseGlob(RuleParameters parameters, string value)
    {
        try
        {
            return GlobPattern.Parse(value);
        }
        catch (ArgumentException)
        {
            throw parameters.Malformed("files", $"'{value}' is not a valid path or glob");
        }
    }

    private static IEnumerable<PolicyViolation> CheckContent(
        PolicyContext context,
        string path,
        IReadOnlyCollection<string> contains,
        IReadOnlyCollection<string> keys)
    {
        var content = context.Tree.Read(path);
        if (content is null)
        {
            yield return new PolicyViolation($"Required file '{path}' could not be read, so its content cannot be verified.", path);
            yield break;
        }

        if (PolicyLimits.IsOversized(content))
        {
            yield return new PolicyViolation(
                $"Required file '{path}' exceeds {PolicyLimits.MaxFileCharacters} characters, so its content cannot be verified.", path);
            yield break;
        }

        foreach (var fragment in contains.Where(f => !content.Contains(f, StringComparison.Ordinal)))
        {
            yield return new PolicyViolation($"Required file '{path}' does not contain '{fragment}'.", path, Evidence: $"missing text: {fragment}");
        }

        if (keys.Count == 0)
        {
            yield break;
        }

        IReadOnlyList<DocNode> documents;
        string? problem = null;
        try
        {
            documents = StructuredDocument.Parse(path, content);
        }
        catch (FormatException ex)
        {
            documents = [];
            problem = ex.Message;
        }

        if (problem is not null)
        {
            yield return new PolicyViolation($"Required file '{path}' could not be parsed, so its keys cannot be verified.", path, Evidence: problem);
            yield break;
        }

        foreach (var key in keys.Where(k => !documents.Any(d => StructuredDocument.HasValue(StructuredDocument.Resolve(d, k)))))
        {
            yield return new PolicyViolation($"Required file '{path}' has no value for key '{key}'.", path, Evidence: $"missing key: {key}");
        }
    }
}
