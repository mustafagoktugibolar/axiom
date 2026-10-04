using Axiom.Domain.Policy.Rules.Support;

namespace Axiom.Domain.Policy.Rules;

/// <summary>Protects paths from being changed (generated code, vendored trees, frozen contracts).</summary>
public sealed class ForbiddenPathChangeRule : BuiltInRule
{
    private static readonly string[] Kinds = ["added", "modified", "deleted", "renamed"];

    public override string RuleId => "forbidden-path-change";

    public override string Version => "1.0.0";

    public override string Description =>
        "Reports every changed file whose path matches a protected glob. A rename is checked at both its old and its new path.";

    public override IReadOnlyDictionary<string, string> Parameters { get; } = Describe(
        ("paths", "Required. Globs of protected paths (newline- or comma-separated)."),
        ("kinds", "Optional. Change kinds that are forbidden: added, modified, deleted, renamed. Default: all."),
        ("exclude", "Optional. Globs exempt from the rule."));

    private protected override IEnumerable<PolicyViolation> Run(PolicyContext context, RuleParameters parameters)
    {
        var paths = parameters.RequiredGlobs("paths");
        var exclude = parameters.Globs("exclude");
        var kinds = parameters.Choices("kinds", Kinds);

        foreach (var change in context.Changes)
        {
            var kind = KindName(change.Kind);
            if (!kinds.Contains(kind))
            {
                continue;
            }

            var candidates = change.Kind == ChangeKind.Renamed && change.PreviousPath is not null
                ? new[] { change.Path, change.PreviousPath }
                : [change.Path];

            foreach (var path in candidates.Distinct(StringComparer.Ordinal))
            {
                var protectedBy = paths.FirstOrDefault(g => g.IsMatch(path));
                if (protectedBy is not null && !exclude.MatchesAny(path))
                {
                    yield return new PolicyViolation(
                        $"'{path}' is a protected path and must not be {kind}.",
                        path,
                        Evidence: $"matches {protectedBy.Value}");
                }
            }
        }
    }
}
