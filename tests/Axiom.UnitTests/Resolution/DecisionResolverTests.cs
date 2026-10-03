using Axiom.Domain.Governance;
using Axiom.Domain.Resolution;
using Axiom.UnitTests.Governance;

namespace Axiom.UnitTests.Resolution;

public class DecisionResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Scope Gateway = Scope.Of((ScopeDimension.Repository, ["gateway"]));

    private static readonly EvaluationScope GatewayChange = EvaluationScope.Empty
        .With(ScopeDimension.Organization, "org:acme")
        .With(ScopeDimension.System, "system:gui-platform")
        .With(ScopeDimension.Component, "component:gui/api-gateway")
        .With(ScopeDimension.Repository, "repo:gateway")
        .With(ScopeDimension.Path, "src/Legacy/Homepage/Resolver.cs");

    private static RecordRevision Rev(GovernanceRecord record) =>
        new(record, new SourceProvenance("governance", "abc123", $"x/{record.Id}.md", "blob"), 1);

    private static ResolutionResult Resolve(EvaluationScope change, bool hardGate, params GovernanceRecord[] records) =>
        DecisionResolver.Resolve(new ResolutionInput([.. records.Select(Rev)], change, Now, hardGate));

    private static ResolutionResult Resolve(params GovernanceRecord[] records) => Resolve(GatewayChange, false, records);

    [Fact]
    public void Selects_accepted_records_whose_scope_matches_by_any_entity_alias()
    {
        var bySimpleName = TestRecords.Decision("ARCH-001", scope: Scope.Of((ScopeDimension.Component, ["api-gateway"])));
        var byQualifiedName = TestRecords.Decision("ARCH-002", scope: Scope.Of((ScopeDimension.Component, ["gui/api-gateway"])));
        var byRef = TestRecords.Decision("ARCH-003", scope: Scope.Of((ScopeDimension.Component, ["component:gui/api-gateway"])));
        var other = TestRecords.Decision("ARCH-004", scope: Scope.Of((ScopeDimension.Component, ["billing"])));

        var result = Resolve(bySimpleName, byQualifiedName, byRef, other);

        Assert.Equal(["ARCH-001", "ARCH-002", "ARCH-003"], result.Applicable.Select(a => a.Revision.Id).Order());
        var excluded = Assert.Single(result.Trace, t => !t.Selected);
        Assert.Equal("ARCH-004", excluded.RecordId);
        Assert.Contains(excluded.Reasons, r => r.Contains("component did not match", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(LifecycleStatus.Proposed)]
    [InlineData(LifecycleStatus.Rejected)]
    [InlineData(LifecycleStatus.Superseded)]
    [InlineData(LifecycleStatus.Deprecated)]
    public void Non_accepted_records_never_govern(LifecycleStatus status)
    {
        var result = Resolve(TestRecords.Decision("ARCH-001", status, Gateway));

        Assert.Empty(result.Applicable);
        Assert.Contains(result.Trace, t => t.RecordId == "ARCH-001" && !t.Selected);
        Assert.Equal(status == LifecycleStatus.Proposed, result.Candidates.Any());
    }

    [Fact]
    public void Unknown_dimension_keeps_the_record_as_potentially_applicable()
    {
        var production = TestRecords.Decision("SEC-001", scope: Scope.Of((ScopeDimension.Environment, ["production"])));

        var applied = Assert.Single(Resolve(production).Applicable);

        Assert.True(applied.IsPotential);
        Assert.Contains(applied.Reasons, r => r.Contains("environment not determined", StringComparison.Ordinal));
        Assert.Empty(Resolve(GatewayChange.With(ScopeDimension.Environment, "staging"), false, production).Applicable);
    }

    [Fact]
    public void Active_exception_waives_its_target_but_leaves_it_visible()
    {
        var decision = TestRecords.Decision("ARCH-042", scope: Gateway);
        var exception = TestRecords.Exception("EXC-023", ["ARCH-042"],
            Scope.Of((ScopeDimension.Repository, ["gateway"]), (ScopeDimension.Path, ["src/Legacy/Homepage/**"])), Now.AddDays(-2), Now.AddDays(30));

        var result = Resolve(decision, exception);

        var applied = Assert.Single(result.Applicable);
        Assert.Equal(["EXC-023"], applied.WaivedBy.AsEnumerable());
        Assert.False(applied.IsEffective);
        Assert.Equal(Importance.Waived, applied.Importance);
        Assert.Equal(Containment.Full, Assert.Single(result.Exceptions).Coverage);
        Assert.Equal("EXC-023", result.WaiverFor("ARCH-042", GatewayChange, null));
    }

    [Fact]
    public void Expired_exception_stops_applying_automatically()
    {
        var decision = TestRecords.Decision("ARCH-042", scope: Gateway);
        var exception = TestRecords.Exception("EXC-023", ["ARCH-042"], Gateway, Now.AddDays(-60), Now);

        var result = Resolve(decision, exception);

        Assert.True(Assert.Single(result.Applicable).IsEffective);
        Assert.Empty(result.Exceptions);
        Assert.Contains(result.Trace, t => t.RecordId == "EXC-023" && t.Reasons.Any(r => r.StartsWith("expired at", StringComparison.Ordinal)));
    }

    [Fact]
    public void Unapproved_exception_does_not_waive()
    {
        var decision = TestRecords.Decision("ARCH-042", scope: Gateway);
        var exception = TestRecords.Exception("EXC-023", ["ARCH-042"], Gateway, Now.AddDays(-1), Now.AddDays(1), LifecycleStatus.Proposed);

        Assert.True(Assert.Single(Resolve(decision, exception).Applicable).IsEffective);
    }

    [Fact]
    public void Exception_never_waives_a_non_exemptable_control()
    {
        var hard = TestRecords.Decision("SEC-001", scope: Gateway, exemptable: false, verdict: EnforcementLevel.Block);
        var exception = TestRecords.Exception("EXC-001", ["SEC-001"], Gateway, Now.AddDays(-1), Now.AddDays(1));

        var result = Resolve(hard, exception);

        var applied = Assert.Single(result.Applicable);
        Assert.True(applied.IsEffective);
        Assert.Equal(PrecedenceRank.HardControl, applied.Rank);
        Assert.Empty(result.Exceptions);
        Assert.Null(result.WaiverFor("SEC-001", GatewayChange, "src/Legacy/Homepage/Resolver.cs"));
    }

    [Fact]
    public void Exception_covering_only_some_paths_waives_only_those_paths()
    {
        var change = GatewayChange.With(ScopeDimension.Path, "src/Routing/New.cs");
        var decision = TestRecords.Decision("ARCH-042", scope: Gateway);
        var exception = TestRecords.Exception("EXC-023", ["ARCH-042"],
            Scope.Of((ScopeDimension.Repository, ["gateway"]), (ScopeDimension.Path, ["src/Legacy/Homepage/**"])), Now.AddDays(-2), Now.AddDays(30));

        var result = Resolve(change, false, decision, exception);

        var applied = Assert.Single(result.Applicable);
        Assert.True(applied.IsEffective);
        Assert.Equal(["EXC-023"], applied.PartiallyWaivedBy.AsEnumerable());
        Assert.Equal("EXC-023", result.WaiverFor("ARCH-042", change, "src/Legacy/Homepage/Resolver.cs"));
        Assert.Null(result.WaiverFor("ARCH-042", change, "src/Routing/New.cs"));
        Assert.Null(result.WaiverFor("ARCH-042", change, null));
    }

    [Fact]
    public void Exception_requires_every_scoped_dimension_to_be_determined()
    {
        var decision = TestRecords.Decision("ARCH-042", scope: Gateway);
        var exception = TestRecords.Exception("EXC-023", ["ARCH-042"],
            Scope.Of((ScopeDimension.Repository, ["gateway"]), (ScopeDimension.Environment, ["staging"])), Now.AddDays(-2), Now.AddDays(30));

        Assert.True(Assert.Single(Resolve(decision, exception).Applicable).IsEffective);
    }

    [Fact]
    public void Explicit_conflict_between_equal_rank_records_requires_review_and_blocks_a_hard_gate()
    {
        var a = TestRecords.Decision("ARCH-001", scope: Gateway, relations: [new Relation(RelationKind.ConflictsWith, "ARCH-002")]);
        var b = TestRecords.Decision("ARCH-002", scope: Gateway);

        var advisory = Assert.Single(Resolve(GatewayChange, false, a, b).Conflicts);
        var gate = Assert.Single(Resolve(GatewayChange, true, a, b).Conflicts);

        Assert.Equal(ConflictCodes.Unresolved, advisory.Code);
        Assert.Equal(EnforcementLevel.RequireReview, advisory.Severity);
        Assert.False(advisory.IsResolved);
        Assert.Equal(EnforcementLevel.Block, gate.Severity);
        Assert.Equal(EnforcementLevel.RequireReview, Resolve(GatewayChange, false, a, b).ConflictSeverity);
    }

    [Fact]
    public void Two_conflicting_non_exemptable_controls_are_a_configuration_error()
    {
        var a = TestRecords.Decision("SEC-001", scope: Gateway, exemptable: false, relations: [new Relation(RelationKind.ConflictsWith, "SEC-002")]);
        var b = TestRecords.Decision("SEC-002", scope: Gateway, exemptable: false);

        var conflict = Assert.Single(Resolve(a, b).Conflicts);

        Assert.Equal(ConflictCodes.ConfigurationError, conflict.Code);
        Assert.Equal(EnforcementLevel.Block, conflict.Severity);
    }

    [Fact]
    public void Hard_control_prevails_over_a_conflicting_exemptable_record()
    {
        var hard = TestRecords.Decision("SEC-001", scope: Gateway, exemptable: false, verdict: EnforcementLevel.Block);
        var soft = TestRecords.Decision("ARCH-002", scope: Gateway, relations: [new Relation(RelationKind.ConflictsWith, "SEC-001")]);

        var result = Resolve(hard, soft);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("SEC-001", conflict.PrevailingRecordId);
        Assert.Null(result.ConflictSeverity);
        Assert.Equal(["SEC-001"], result.Applicable.Single(a => a.Revision.Id == "ARCH-002").OverriddenBy.AsEnumerable());
        Assert.True(result.Applicable.Single(a => a.Revision.Id == "SEC-001").IsEffective);
    }

    [Fact]
    public void Structural_contradiction_is_detected_without_an_explicit_relation()
    {
        var forbids = TestRecords.Decision("ARCH-001", scope: Gateway) with { Forbidden = ["Use Redis for session state."] };
        var prefers = TestRecords.Decision("ARCH-002", scope: Gateway) with { Preferred = ["use  redis for session state"] };

        var conflict = Assert.Single(Resolve(forbids, prefers).Conflicts);

        Assert.Equal(ConflictSource.Structural, conflict.Source);
        Assert.Equal(ConflictCodes.Unresolved, conflict.Code);
    }

    [Fact]
    public void Conflict_with_a_waived_record_is_not_reported()
    {
        var a = TestRecords.Decision("ARCH-001", scope: Gateway, relations: [new Relation(RelationKind.ConflictsWith, "ARCH-002")]);
        var b = TestRecords.Decision("ARCH-002", scope: Gateway);
        var exception = TestRecords.Exception("EXC-001", ["ARCH-002"], Gateway, Now.AddDays(-1), Now.AddDays(1));

        Assert.Empty(Resolve(a, b, exception).Conflicts);
    }

    [Fact]
    public void Declared_refinement_within_scope_replaces_the_broader_exemptable_record()
    {
        var broad = TestRecords.Decision("STD-001", kind: RecordKind.Standard, relations: [new Relation(RelationKind.ConflictsWith, "ARCH-002")]);
        var specific = TestRecords.Decision("ARCH-002", scope: Gateway, relations: [new Relation(RelationKind.Refines, "STD-001")]);

        var result = Resolve(broad, specific);

        Assert.Equal(["ARCH-002"], result.Applicable.Single(a => a.Revision.Id == "STD-001").RefinedBy.AsEnumerable());
        Assert.True(result.Applicable.Single(a => a.Revision.Id == "ARCH-002").IsEffective);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Refinement_cannot_displace_a_non_exemptable_control()
    {
        var hard = TestRecords.Decision("SEC-001", exemptable: false, verdict: EnforcementLevel.Block);
        var specific = TestRecords.Decision("ARCH-002", scope: Gateway, relations: [new Relation(RelationKind.Refines, "SEC-001")]);

        var result = Resolve(hard, specific);

        Assert.All(result.Applicable, a => Assert.True(a.IsEffective));
    }

    [Fact]
    public void More_specific_record_does_not_override_without_a_declared_refinement()
    {
        var broad = TestRecords.Decision("STD-001", kind: RecordKind.Standard, relations: [new Relation(RelationKind.ConflictsWith, "ARCH-002")]);
        var specific = TestRecords.Decision("ARCH-002", scope: Gateway);

        var result = Resolve(broad, specific);

        Assert.All(result.Applicable, a => Assert.True(a.IsEffective));
        Assert.Equal(ConflictCodes.Unresolved, Assert.Single(result.Conflicts).Code);
    }

    [Fact]
    public void Results_are_ordered_by_precedence_then_specificity()
    {
        var advisory = TestRecords.Decision("ADV-001", level: AuthorityLevel.Advisory, verdict: EnforcementLevel.Info);
        var principle = TestRecords.Decision("PRIN-001", kind: RecordKind.Principle, level: AuthorityLevel.Organization, verdict: EnforcementLevel.Warn);
        var orgStandard = TestRecords.Decision("STD-001", kind: RecordKind.Standard, level: AuthorityLevel.Organization);
        var repoDecision = TestRecords.Decision("ARCH-001", scope: Gateway, level: AuthorityLevel.Organization);
        var hard = TestRecords.Decision("SEC-001", exemptable: false, level: AuthorityLevel.RegulatorySecurityHard, verdict: EnforcementLevel.Block);

        var result = Resolve(advisory, principle, orgStandard, repoDecision, hard);

        Assert.Equal(["SEC-001", "ARCH-001", "STD-001", "PRIN-001", "ADV-001"], result.Applicable.Select(a => a.Revision.Id));
        Assert.Equal([Importance.Required, Importance.Required, Importance.Required, Importance.Recommended, Importance.Informational],
            result.Applicable.Select(a => a.Importance));
    }

    [Fact]
    public void Every_considered_record_has_exactly_one_trace_entry()
    {
        var records = new[]
        {
            TestRecords.Decision("ARCH-001", scope: Gateway),
            TestRecords.Decision("ARCH-002", LifecycleStatus.Rejected),
            TestRecords.Decision("ARCH-003", scope: Scope.Of((ScopeDimension.Repository, ["other"]))),
            TestRecords.Exception("EXC-001", ["ARCH-001"], Gateway, Now.AddDays(-1), Now.AddDays(1)),
        };

        var result = Resolve(records);

        Assert.Equal(records.Select(r => r.Id).Order(), result.Trace.Select(t => t.RecordId));
        Assert.Equal([0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0], result.Trace.Single(t => t.RecordId == "ARCH-001").Specificity.AsEnumerable());
    }
}
