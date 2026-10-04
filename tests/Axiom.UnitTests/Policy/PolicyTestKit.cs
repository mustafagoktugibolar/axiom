using System.Collections.Immutable;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;
using Axiom.Domain.Policy.Rules;
using Axiom.Domain.Resolution;

namespace Axiom.UnitTests.Policy;

internal static class PolicyTestKit
{
    public static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    public static readonly EvaluationScope GatewayChange = EvaluationScope.Empty
        .With(ScopeDimension.Organization, "org:acme")
        .With(ScopeDimension.Repository, "repo:gateway")
        .With(ScopeDimension.Path, "src/Api/Program.cs");

    public static ChangedFile Added(string path, string content) =>
        new(path, ChangeKind.Added, null, content, null, [.. content.Split('\n').Select((text, i) => new AddedLine(i + 1, text))]);

    public static ChangedFile Modified(string path, string content, string previous, params AddedLine[] added) =>
        new(path, ChangeKind.Modified, null, content, previous, [.. added]);

    public static ChangedFile Deleted(string path) => new(path, ChangeKind.Deleted, null, null, null, []);

    public static ChangedFile Renamed(string from, string to) => new(to, ChangeKind.Renamed, from, "", "", []);

    public static PolicyContext Context(params ChangedFile[] changes) =>
        new("gateway", [.. changes], InMemoryRepositoryView.FromChanges(changes), GatewayChange);

    public static IReadOnlyDictionary<string, string> Args(params (string Name, string Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);

    public static ImmutableArray<PolicyViolation> Run(IPolicyRule rule, PolicyContext context, params (string Name, string Value)[] parameters) =>
        [.. rule.Evaluate(context, Args(parameters))];

    public static RuleBinding Bind(string ruleId, EnforcementLevel mode, params (string Name, string Value)[] parameters) =>
        new(ruleId, mode, parameters.ToImmutableSortedDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal));

    public static ResolutionResult Resolve(params GovernanceRecord[] records) =>
        DecisionResolver.Resolve(new ResolutionInput(
            [.. records.Select(r => new RecordRevision(r, new SourceProvenance("governance", "abc123", $"x/{r.Id}.md", "blob"), 1))],
            GatewayChange,
            Now,
            false));
}
