using System.Collections.Immutable;
using Axiom.Domain.Governance;

namespace Axiom.UnitTests.Governance;

/// <summary>Builders for governance records used across unit tests.</summary>
internal static class TestRecords
{
    public static GovernanceRecord Decision(
        string id,
        LifecycleStatus status = LifecycleStatus.Accepted,
        Scope? scope = null,
        bool exemptable = true,
        AuthorityLevel level = AuthorityLevel.System,
        EnforcementLevel verdict = EnforcementLevel.RequireReview,
        RecordKind kind = RecordKind.Decision,
        IEnumerable<Relation>? relations = null,
        IEnumerable<RuleBinding>? rules = null,
        Validity? validity = null,
        string[]? owners = null) => new()
        {
            Id = id,
            Kind = kind,
            SchemaVersion = "axiom.io/v1",
            Title = $"Record {id}",
            Owners = [.. owners ?? ["platform-architecture"]],
            Status = status,
            Scope = scope ?? Scope.Unrestricted,
            Authority = new Authority(level, exemptable),
            Enforcement = new Enforcement(verdict, [.. rules ?? []]),
            Statement = $"Statement of {id}",
            Relations = [.. relations ?? []],
            Validity = validity ?? Validity.Unbounded,
            ContentHash = $"hash-{id}-{status}",
        };

    public static GovernanceRecord Exception(
        string id,
        string[] targets,
        Scope scope,
        DateTimeOffset startsAt,
        DateTimeOffset expiresAt,
        LifecycleStatus status = LifecycleStatus.Accepted,
        string[]? approvers = null) => new()
        {
            Id = id,
            Kind = RecordKind.Exception,
            SchemaVersion = "axiom.io/v1",
            Title = $"Exception {id}",
            Owners = ["gui-platform"],
            Status = status,
            Scope = scope,
            Authority = new Authority(AuthorityLevel.Advisory, true),
            Enforcement = new Enforcement(EnforcementLevel.Info, []),
            Statement = "Temporary deviation.",
            Relations = [.. targets.Select(t => new Relation(RelationKind.ExceptionTo, t))],
            Exception = new ExceptionTerms([.. targets], "Temporary deviation.", startsAt, expiresAt, [.. approvers ?? ["platform-architecture"]], null, ImmutableArray<string>.Empty),
            ContentHash = $"hash-{id}-{status}",
        };
}
