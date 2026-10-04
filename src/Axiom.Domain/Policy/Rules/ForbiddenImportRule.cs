using Axiom.Domain.Policy.Rules.Support;

namespace Axiom.Domain.Policy.Rules;

/// <summary>Forbidden imports: namespaces or modules that must not be used anywhere in scope.</summary>
public sealed class ForbiddenImportRule : BuiltInRule
{
    public override string RuleId => "forbidden-import";

    public override string Version => "1.0.0";

    public override string Description =>
        "Changed source files must not import or reference namespaces/modules matching 'patterns'. Understands "
        + ImportAnalysis.Languages + ". Comments are ignored. Added and renamed files are evaluated in full, modified files on their added lines.";

    public override IReadOnlyDictionary<string, string> Parameters { get; } = Describe(
        ("patterns", "Required. Forbidden namespace/module patterns. '*' matches any characters; a pattern without a "
            + "trailing '*' also covers everything nested below it (lodash covers lodash/fp)."),
        ("paths", "Optional. Globs limiting which files are checked. Default: every changed file."),
        ("allowed", "Optional. Patterns that are permitted even though they match 'patterns'."),
        ("exclude", "Optional. Globs exempt from the rule."));

    private protected override IEnumerable<PolicyViolation> Run(PolicyContext context, RuleParameters parameters)
    {
        var forbidden = ImportAnalysis.Patterns(parameters.RequiredList("patterns"));
        var allowed = ImportAnalysis.Patterns(parameters.List("allowed"));
        var paths = parameters.Globs("paths");
        var exclude = parameters.Globs("exclude");

        return ChangeSet.Present(context, paths, exclude)
            .SelectMany(file => ImportAnalysis.Find(file, forbidden, allowed, "which is a forbidden import"));
    }
}
