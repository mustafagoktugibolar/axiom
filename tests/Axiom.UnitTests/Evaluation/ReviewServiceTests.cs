using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Application.Review;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Axiom.Domain.Review;
using Microsoft.Extensions.Time.Testing;

namespace Axiom.UnitTests.Evaluation;

public class ReviewServiceTests
{
    private const string Org = "acme";
    private readonly InMemoryEvaluationStore _evaluations = new();
    private readonly InMemoryReviewStore _reviews = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly ReviewService _service;

    public ReviewServiceTests() => _service = new ReviewService(_evaluations, _reviews, _time);

    private sealed class InMemoryReviewStore : IReviewStore
    {
        public List<ReviewDecision> Decisions { get; } = [];

        public Task AppendAsync(ReviewDecision decision, CancellationToken cancellationToken)
        {
            Decisions.Add(decision);
            return Task.CompletedTask;
        }

        public Task<ImmutableArray<ReviewDecision>> ListAsync(string organizationId, string evaluationId, CancellationToken cancellationToken) =>
            Task.FromResult(Decisions.Where(d => d.OrganizationId == organizationId && d.EvaluationId == evaluationId).ToImmutableArray());

        public Task<ImmutableArray<ReviewQueueItem>> ListPendingAsync(string organizationId, int skip, int take, CancellationToken cancellationToken) =>
            Task.FromResult(ImmutableArray<ReviewQueueItem>.Empty);
    }

    private static AxiomPrincipal Reviewer(string subject = "user:bob", Role role = Role.DecisionOwner, params string[] teams) =>
        new(subject, null, Org, [role], [.. teams]);

    private async Task<EvaluationRecord> StoreAsync(Verdict verdict, string id = "ev_1", string? designHash = "dh1", bool significant = false)
    {
        var severity = verdict switch { Verdict.Block => EnforcementLevel.Block, Verdict.RequireReview => EnforcementLevel.RequireReview, _ => EnforcementLevel.Info };
        var evaluation = new EvaluationRecord
        {
            Id = id,
            OrganizationId = Org,
            Stage = EvaluationStage.Design,
            CreatedAt = _time.GetUtcNow(),
            Actor = new ActorIdentity("user:alice", null, "kiro", null),
            Scm = new ScmCoordinates("repo:gateway", "main", null, null, null),
            RequestFingerprint = "fp-" + id,
            SnapshotId = "gs_1",
            SourceCommit = "c1",
            SnapshotPublishedAt = _time.GetUtcNow(),
            CatalogVersion = "cv_1",
            DesignHash = designHash,
            ResolvedScope = new ResolvedScopeView(ImmutableSortedDictionary<string, ImmutableArray<string>>.Empty, []),
            Significance = new SignificanceAssessment(significant, [], []),
            Findings =
            [
                new Finding { Code = "SEMANTIC_CONFLICT", Severity = severity, Source = FindingSource.Semantic, Message = "m", GovernanceIds = ["ARCH-042"] },
            ],
            RequiredReviewers = ["platform-architecture"],
            Verdict = verdict,
        };
        await _evaluations.AppendAsync(evaluation, [], default);
        return evaluation;
    }

    [Fact]
    public async Task Routed_owner_can_approve_and_the_evaluation_is_cleared()
    {
        await StoreAsync(Verdict.RequireReview);

        var decision = await _service.ReviewAsync(Reviewer(teams: "platform-architecture"), new ReviewRequest("ev_1", true, "Consistent with ARCH-042 after discussion."), default);
        var state = await _service.GetAsync(Reviewer(role: Role.Auditor), "ev_1", default);

        Assert.Equal(("dh1", ReviewOutcome.Approved, "user:bob"), (decision.SubjectHash, decision.Outcome, decision.Reviewer));
        Assert.Equal(ClearanceState.Approved, state.Clearance.State);
        Assert.True(state.Clearance.AllowsProgress);
    }

    [Fact]
    public async Task Approval_does_not_carry_over_to_a_materially_changed_design()
    {
        var original = await StoreAsync(Verdict.RequireReview);
        await _service.ReviewAsync(Reviewer(role: Role.Approver), new ReviewRequest("ev_1", true, "Approved as designed today."), default);

        var changed = original with { DesignHash = "dh2" };

        Assert.Equal(ClearanceState.Approved, ReviewRules.ClearanceOf(original, _reviews.Decisions).State);
        Assert.Equal(ClearanceState.Pending, ReviewRules.ClearanceOf(changed, _reviews.Decisions).State);
    }

    [Fact]
    public async Task Latest_decision_wins()
    {
        await StoreAsync(Verdict.RequireReview);
        await _service.ReviewAsync(Reviewer(role: Role.Approver), new ReviewRequest("ev_1", true, "Looks acceptable to me."), default);
        _time.Advance(TimeSpan.FromMinutes(1));
        await _service.ReviewAsync(Reviewer("user:carol", Role.Approver), new ReviewRequest("ev_1", false, "Violates the routing decision."), default);

        var state = await _service.GetAsync(Reviewer(role: Role.Auditor), "ev_1", default);

        Assert.Equal(ClearanceState.Rejected, state.Clearance.State);
        Assert.False(state.Clearance.AllowsProgress);
        Assert.Equal(2, state.Decisions.Length);
    }

    [Fact]
    public async Task Block_verdict_cannot_be_cleared_by_review()
    {
        var blocked = await StoreAsync(Verdict.Block);

        var error = await Assert.ThrowsAsync<AxiomException>(() =>
            _service.ReviewAsync(Reviewer(role: Role.Approver), new ReviewRequest("ev_1", true, "Please let this through."), default));

        Assert.Equal(ErrorKind.Conflict, error.Kind);
        Assert.Equal(ClearanceState.Blocked, ReviewRules.ClearanceOf(blocked, []).State);
        Assert.Empty(_reviews.Decisions);
    }

    [Fact]
    public async Task Author_cannot_review_their_own_change()
    {
        await StoreAsync(Verdict.RequireReview);
        var error = await Assert.ThrowsAsync<AxiomException>(() =>
            _service.ReviewAsync(Reviewer("user:alice", Role.Approver), new ReviewRequest("ev_1", true, "I approve my own change."), default));
        Assert.Equal(ErrorKind.Forbidden, error.Kind);
    }

    [Fact]
    public async Task Decision_owner_outside_the_routed_teams_cannot_review()
    {
        await StoreAsync(Verdict.RequireReview);
        var error = await Assert.ThrowsAsync<AxiomException>(() =>
            _service.ReviewAsync(Reviewer(teams: "billing"), new ReviewRequest("ev_1", true, "Approving from another team."), default));
        Assert.Equal(ErrorKind.Forbidden, error.Kind);
    }

    [Theory]
    [InlineData(Role.Contributor)]
    [InlineData(Role.Reader)]
    [InlineData(Role.ExceptionApprover)]
    public async Task Roles_without_review_rights_are_rejected(Role role)
    {
        await StoreAsync(Verdict.RequireReview);
        var error = await Assert.ThrowsAsync<AxiomException>(() =>
            _service.ReviewAsync(Reviewer(role: role, teams: "platform-architecture"), new ReviewRequest("ev_1", true, "Trying to approve this."), default));
        Assert.Equal(ErrorKind.Forbidden, error.Kind);
    }

    [Fact]
    public async Task Review_requires_a_rationale()
    {
        await StoreAsync(Verdict.RequireReview);
        var error = await Assert.ThrowsAsync<AxiomException>(() =>
            _service.ReviewAsync(Reviewer(role: Role.Approver), new ReviewRequest("ev_1", true, "ok"), default));
        Assert.Equal(ErrorKind.Validation, error.Kind);
    }

    [Fact]
    public async Task Reviewer_from_another_organization_cannot_see_the_evaluation()
    {
        await StoreAsync(Verdict.RequireReview);
        var outsider = new AxiomPrincipal("user:eve", null, "globex", [Role.Approver], []);
        var error = await Assert.ThrowsAsync<AxiomException>(() => _service.ReviewAsync(outsider, new ReviewRequest("ev_1", true, "Approving across tenants."), default));
        Assert.Equal(ErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task Overturning_a_finding_records_feedback_without_clearing_the_evaluation()
    {
        var evaluation = await StoreAsync(Verdict.RequireReview);

        var decision = await _service.OverturnFindingAsync(Reviewer(role: Role.Contributor), new FindingOverturnRequest("ev_1", "SEMANTIC_CONFLICT", "The gateway only forwards the request."), default);

        Assert.Equal((ReviewKind.FindingOverturn, "SEMANTIC_CONFLICT"), (decision.Kind, decision.FindingCode));
        Assert.Equal(ClearanceState.Pending, ReviewRules.ClearanceOf(evaluation, _reviews.Decisions).State);
    }

    [Fact]
    public async Task Classification_override_is_recorded_for_significant_evaluations_only()
    {
        var significant = await StoreAsync(Verdict.AllowWithWarnings, "ev_sig", significant: true);
        await StoreAsync(Verdict.Allow, "ev_plain");

        await _service.OverrideClassificationAsync(Reviewer(role: Role.Approver), "ev_sig", "Config-only change inside an existing pattern.", default);

        Assert.True(ReviewRules.IsSignificanceOverridden(significant, _reviews.Decisions));
        await Assert.ThrowsAsync<AxiomException>(() => _service.OverrideClassificationAsync(Reviewer(role: Role.Approver), "ev_plain", "Not significant anyway, override.", default));
    }
}
