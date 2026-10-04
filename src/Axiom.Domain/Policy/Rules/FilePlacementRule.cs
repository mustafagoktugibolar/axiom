using Axiom.Domain.Governance;
using Axiom.Domain.Policy.Rules.Support;

namespace Axiom.Domain.Policy.Rules;

/// <summary>Repository convention: files of a given kind must live under designated roots.</summary>
public sealed class FilePlacementRule : BuiltInRule
{
    public override string RuleId => "file-placement";

    public override string Version => "1.0.0";

    public override string Description =>
        "Added, modified or renamed files matching 'match' must be located under one of 'allowedRoots'.";

    public override IReadOnlyDictionary<string, string> Parameters { get; } = Describe(
        ("match", "Required. Globs selecting the files the convention applies to, e.g. **/*Controller.cs."),
        ("allowedRoots", "Required. Directories (globs allowed) the files must be under, e.g. src/Api/Controllers."),
        ("exclude", "Optional. Globs exempt from the rule."));

    private protected override IEnumerable<PolicyViolation> Run(PolicyContext context, RuleParameters parameters)
    {
        var match = parameters.RequiredGlobs("match");
        var exclude = parameters.Globs("exclude");
        var roots = parameters.RequiredList("allowedRoots");
        var underRoot = parameters.RequiredGlobs("allowedRoots")
            .Select(root => GlobPattern.Parse(root.Value + "/**"))
            .ToArray();

        foreach (var file in ChangeSet.Present(context, match, exclude))
        {
            if (!underRoot.Any(root => root.IsMatch(file.Path)))
            {
                yield return new PolicyViolation(
                    $"'{file.Path}' must be placed under one of: {string.Join(", ", roots)}.",
                    file.Path,
                    Evidence: $"allowed roots: {string.Join(", ", roots)}");
            }
        }
    }
}
