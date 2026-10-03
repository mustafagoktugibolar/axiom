using Axiom.Domain.Governance;

namespace Axiom.UnitTests.Governance;

public class GovernanceSetValidatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Scope GatewayRepo = Scope.Of((ScopeDimension.Repository, ["gateway"]));

    private static IEnumerable<string> ErrorCodes(params GovernanceRecord[] records) =>
        GovernanceSetValidator.Validate(records).Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Code);

    [Fact]
    public void Valid_set_has_no_errors()
    {
        var decision = TestRecords.Decision("ARCH-042", scope: GatewayRepo);
        var exception = TestRecords.Exception("EXC-023", ["ARCH-042"],
            Scope.Of((ScopeDimension.Repository, ["gateway"]), (ScopeDimension.Path, ["src/Legacy/**"])), Start, Start.AddMonths(3));

        Assert.Empty(ErrorCodes(decision, exception));
    }

    [Fact]
    public void Duplicate_ids_are_rejected() =>
        Assert.Contains(IssueCodes.DuplicateId, ErrorCodes(TestRecords.Decision("ARCH-001"), TestRecords.Decision("ARCH-001")));

    [Fact]
    public void Accepted_record_without_owner_is_an_error() =>
        Assert.Contains(IssueCodes.MissingOwner, ErrorCodes(TestRecords.Decision("ARCH-001", owners: [])));

    [Fact]
    public void Exception_cannot_target_non_exemptable_record()
    {
        var hard = TestRecords.Decision("SEC-001", scope: GatewayRepo, exemptable: false, verdict: EnforcementLevel.Block);
        var exception = TestRecords.Exception("EXC-001", ["SEC-001"], GatewayRepo, Start, Start.AddDays(30));

        Assert.Contains(IssueCodes.ExceptionTargetsNonExemptable, ErrorCodes(hard, exception));
    }

    [Fact]
    public void Exception_without_a_scope_is_rejected()
    {
        var decision = TestRecords.Decision("ARCH-042", scope: GatewayRepo);
        var exception = TestRecords.Exception("EXC-001", ["ARCH-042"], Scope.Unrestricted, Start, Start.AddDays(30));

        Assert.Contains(IssueCodes.ExceptionScopeTooBroad, ErrorCodes(decision, exception));
    }

    [Theory]
    [InlineData("gateway", "src/Legacy/**", true)]
    [InlineData("other-repo", "src/Legacy/**", false)]
    [InlineData("gateway", "tests/**", false)]
    public void Exception_must_stay_within_the_target_on_shared_dimensions(string repository, string path, bool valid)
    {
        var decision = TestRecords.Decision("ARCH-042", scope: Scope.Of(
            (ScopeDimension.System, ["gui-platform"]), (ScopeDimension.Repository, ["gateway"]), (ScopeDimension.Path, ["src/**"])));
        var exception = TestRecords.Exception("EXC-001", ["ARCH-042"],
            Scope.Of((ScopeDimension.Repository, [repository]), (ScopeDimension.Path, [path])), Start, Start.AddDays(30));

        Assert.Equal(valid, !ErrorCodes(decision, exception).Contains(IssueCodes.ExceptionScopeTooBroad));
    }

    [Fact]
    public void Exception_must_expire_after_it_starts()
    {
        var decision = TestRecords.Decision("ARCH-042", scope: GatewayRepo);
        var exception = TestRecords.Exception("EXC-001", ["ARCH-042"], GatewayRepo, Start, Start);

        Assert.Contains(IssueCodes.ExceptionWindowInvalid, ErrorCodes(decision, exception));
    }

    [Fact]
    public void Accepted_exception_requires_an_approver()
    {
        var decision = TestRecords.Decision("ARCH-042", scope: GatewayRepo);
        var exception = TestRecords.Exception("EXC-001", ["ARCH-042"], GatewayRepo, Start, Start.AddDays(1), approvers: []);

        Assert.Contains(IssueCodes.ExceptionMissingApprover, ErrorCodes(decision, exception));
    }

    [Fact]
    public void Unknown_relation_target_is_an_error_except_for_related()
    {
        var refines = TestRecords.Decision("ARCH-001", relations: [new Relation(RelationKind.Refines, "ARCH-999")]);
        var related = TestRecords.Decision("ARCH-002", relations: [new Relation(RelationKind.Related, "SYS-GUI-001")]);

        Assert.Contains(IssueCodes.UnknownRelationTarget, ErrorCodes(refines));
        Assert.Empty(ErrorCodes(related));
    }

    [Fact]
    public void Superseded_record_must_name_its_successor()
    {
        var orphan = TestRecords.Decision("ARCH-001", LifecycleStatus.Superseded);
        Assert.Contains(IssueCodes.SupersededWithoutSuccessor, ErrorCodes(orphan));

        var successor = TestRecords.Decision("ARCH-002", relations: [new Relation(RelationKind.Supersedes, "ARCH-001")]);
        Assert.Empty(ErrorCodes(orphan, successor));
    }

    [Fact]
    public void Accepted_record_cannot_supersede_a_record_that_still_governs()
    {
        var old = TestRecords.Decision("ARCH-001");
        var successor = TestRecords.Decision("ARCH-002", relations: [new Relation(RelationKind.Supersedes, "ARCH-001")]);

        Assert.Contains(IssueCodes.SupersessionMismatch, ErrorCodes(old, successor));
    }

    [Fact]
    public void Supersession_cycles_are_detected()
    {
        var a = TestRecords.Decision("ARCH-001", LifecycleStatus.Superseded, relations: [new Relation(RelationKind.Supersedes, "ARCH-002")]);
        var b = TestRecords.Decision("ARCH-002", LifecycleStatus.Superseded, relations: [new Relation(RelationKind.Supersedes, "ARCH-001")]);

        Assert.Contains(IssueCodes.SupersessionCycle, ErrorCodes(a, b));
    }

    [Fact]
    public void Refinement_may_not_weaken_a_non_exemptable_control()
    {
        var hard = TestRecords.Decision("SEC-001", exemptable: false, verdict: EnforcementLevel.Block);
        var weaker = TestRecords.Decision("ARCH-002", verdict: EnforcementLevel.Warn, relations: [new Relation(RelationKind.Refines, "SEC-001")]);

        Assert.Contains(IssueCodes.RefinementWeakensHardControl, ErrorCodes(hard, weaker));
    }

    [Theory]
    [InlineData(LifecycleStatus.Proposed, LifecycleStatus.Accepted, true)]
    [InlineData(LifecycleStatus.Proposed, LifecycleStatus.Rejected, true)]
    [InlineData(LifecycleStatus.Accepted, LifecycleStatus.Deprecated, true)]
    [InlineData(LifecycleStatus.Accepted, LifecycleStatus.Superseded, true)]
    [InlineData(LifecycleStatus.Deprecated, LifecycleStatus.Superseded, true)]
    [InlineData(LifecycleStatus.Rejected, LifecycleStatus.Accepted, false)]
    [InlineData(LifecycleStatus.Superseded, LifecycleStatus.Accepted, false)]
    [InlineData(LifecycleStatus.Accepted, LifecycleStatus.Proposed, false)]
    [InlineData(LifecycleStatus.Deprecated, LifecycleStatus.Accepted, false)]
    public void Lifecycle_transitions_follow_the_state_diagram(LifecycleStatus from, LifecycleStatus to, bool legal)
    {
        Assert.Equal(legal, LifecycleRules.CanTransition(from, to));
        var issue = GovernanceSetValidator.ValidateTransition(TestRecords.Decision("ARCH-001", from), TestRecords.Decision("ARCH-001", to));
        Assert.Equal(legal, issue is null);
    }

    [Fact]
    public void Authority_property_only_accepted_records_govern()
    {
        var today = new DateOnly(2026, 10, 3);
        foreach (var status in Enum.GetValues<LifecycleStatus>())
        {
            var record = TestRecords.Decision("ARCH-001", status);
            Assert.Equal(status == LifecycleStatus.Accepted, record.IsAuthoritativeOn(today));
        }

        Assert.True((TestRecords.Decision("ARCH-001", LifecycleStatus.Deprecated) with { GovernsExistingCode = true }).IsAuthoritativeOn(today));
        Assert.False(TestRecords.Decision("ARCH-001", validity: new Validity(today.AddDays(1), null, null)).IsAuthoritativeOn(today));
    }

    [Fact]
    public void Exception_is_active_only_inside_its_half_open_window()
    {
        var exception = TestRecords.Exception("EXC-001", ["ARCH-042"], GatewayRepo, Start, Start.AddDays(10));

        Assert.False(exception.IsActiveExceptionAt(Start.AddSeconds(-1)));
        Assert.True(exception.IsActiveExceptionAt(Start));
        Assert.True(exception.IsActiveExceptionAt(Start.AddDays(10).AddSeconds(-1)));
        Assert.False(exception.IsActiveExceptionAt(Start.AddDays(10)));
        Assert.False((exception with { Status = LifecycleStatus.Proposed }).IsActiveExceptionAt(Start.AddDays(1)));
    }

    [Fact]
    public void Snapshot_id_depends_only_on_record_content()
    {
        static RecordRevision Rev(GovernanceRecord r, string commit) => new(r, new SourceProvenance("gov", commit, $"decisions/{r.Id}.md", "blob"), 1);
        var a = TestRecords.Decision("ARCH-001");
        var b = TestRecords.Decision("ARCH-002");

        var first = GovernanceSnapshot.Create("acme", "c1", [Rev(a, "c1"), Rev(b, "c1")]);
        var reordered = GovernanceSnapshot.Create("acme", "c2", [Rev(b, "c2"), Rev(a, "c2")]);
        var changed = GovernanceSnapshot.Create("acme", "c1", [Rev(a, "c1"), Rev(b with { ContentHash = "other" }, "c1")]);
        var otherOrg = GovernanceSnapshot.Create("globex", "c1", [Rev(a, "c1"), Rev(b, "c1")]);

        Assert.Equal(first.Id, reordered.Id);
        Assert.NotEqual(first.Id, changed.Id);
        Assert.NotEqual(first.Id, otherOrg.Id);
        Assert.StartsWith("gs_", first.Id, StringComparison.Ordinal);
    }
}
