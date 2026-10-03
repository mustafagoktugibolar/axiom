using Axiom.Domain.Policy.Rules.Support;

namespace Axiom.Domain.Policy.Rules;

/// <summary>Dependency direction: code in one layer or module must not reference another.</summary>
public sealed class DependencyDirectionRule : BuiltInRule
{
    public override string RuleId => "dependency-direction";

    public override string Version => "1.0.0";

    public override string Description =>
        "Source files under 'from' must not reference namespaces, modules or projects matching 'forbidden'. Understands "
        + ImportAnalysis.Languages + ". Comments are ignored. Added and renamed files are evaluated in full, modified files on their added lines.";

    public override IReadOnlyDictionary<string, string> Parameters { get; } = Describe(
        ("from", "Required. Globs of the source files whose dependencies are restricted, e.g. src/Domain/**."),
        ("forbidden", "Required. Namespace/module patterns that must not be referenced. '*' matches any characters; "
            + "a pattern without a trailing '*' also covers everything nested below it (Company.Infrastructure covers Company.Infrastructure.Sql)."),
        ("allowed", "Optional. Patterns that are permitted even though they match 'forbidden'."),
        ("exclude", "Optional. Globs under 'from' that are exempt."));

    private protected override IEnumerable<PolicyViolation> Run(PolicyContext context, RuleParameters parameters)
    {
        var sources = parameters.RequiredGlobs("from");
        var forbidden = ImportAnalysis.Patterns(parameters.RequiredList("forbidden"));
        var allowed = ImportAnalysis.Patterns(parameters.List("allowed"));
        var exclude = parameters.Globs("exclude");

        return ChangeSet.Present(context, sources, exclude)
            .SelectMany(file => ImportAnalysis.Find(file, forbidden, allowed, "which this location must not depend on"));
    }
}
