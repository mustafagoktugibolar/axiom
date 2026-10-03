using System.Collections.Immutable;

namespace Axiom.Domain.Policy.Rules;

/// <summary>The trusted built-in rule set (design.md §4.5), ordered by rule ID.</summary>
public static class BuiltInRules
{
    public static ImmutableArray<IPolicyRule> All { get; } =
    [
        .. new IPolicyRule[]
        {
            new ApiCompatibilityRule(),
            new DependencyDirectionRule(),
            new DeploymentSecurityRule(),
            new FilePlacementRule(),
            new ForbiddenImportRule(),
            new ForbiddenPackageRule(),
            new ForbiddenPathChangeRule(),
            new NoSecretsRule(),
            new RequiredFileRule(),
            new RequiredMetadataRule(),
            new RequiredTestsRule(),
            new SchemaVersioningRule(),
        }.OrderBy(r => r.RuleId, StringComparer.Ordinal),
    ];
}
