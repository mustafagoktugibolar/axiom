using Axiom.Application.Candidates;
using Axiom.Application.Common;
using Axiom.Domain.Governance;
using Axiom.Infrastructure.Governance.Parsing;
using Axiom.UnitTests.Evaluation;
using Microsoft.Extensions.Time.Testing;

namespace Axiom.UnitTests.Candidates;

public class CandidateDecisionTests
{
    private const string Org = "acme";
    private const string Comment = "Reviewed against the ADR and the current platform direction.";
    private readonly InMemoryCandidateStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly CandidateService _service;
    private readonly AxiomPrincipal _admin = new("user:admin", "Admin", Org, [Role.PlatformAdmin], []);
    private readonly AxiomPrincipal _approver = new("user:ada", "Ada", Org, [Role.Approver], ["platform-architecture"]);
    private readonly AxiomPrincipal _agent = new("agent:kiro", "Kiro", Org, [Role.Contributor], ["payments"]);

    public CandidateDecisionTests() => _service = new CandidateService(_store, _time);

    private static AgentProposalInput Proposal(string statement = "Use PostgreSQL for all services.", string? owner = "platform-architecture", Dictionary<string, string[]>? scope = null) =>
        new("Use PostgreSQL", statement, "We saw three services on three databases.", "gateway:docs/notes.md@abc", owner, null, scope ?? new() { ["repositories"] = ["gateway"] });

    private async Task<string> ProposedAsync(AxiomPrincipal? by = null, string? owner = "platform-architecture")
    {
        var result = await _service.ProposeAsync(by ?? _agent, Proposal(owner: owner), default);
        return result.Candidate.Id;
    }

    [Fact]
    public async Task An_agent_proposal_is_a_proposed_non_authoritative_candidate_with_the_agent_method()
    {
        var result = await _service.ProposeAsync(_agent, Proposal(), default);

        Assert.True(result.Created);
        Assert.Equal(CandidateState.Proposed, result.Candidate.State);
        Assert.Equal(CandidateService.AgentProposalMethod, result.Candidate.Evidence.ExtractionMethod);
        Assert.Equal("gateway:docs/notes.md@abc", result.Candidate.Evidence.SourceLocator);
        Assert.Equal("agent:kiro", result.Candidate.CreatedBy);
        Assert.False(result.Candidate.IsAuthoritative);
        Assert.Contains(_store.Events, e => e.EventType == EventTypes.CandidateDecisionCreated);

        var again = await _service.ProposeAsync(_agent, Proposal(), default);
        Assert.False(again.Created);
        Assert.Single(_store.Items);
    }

    [Fact]
    public async Task Proposals_are_validated_bounded_and_need_the_propose_right()
    {
        var reader = new AxiomPrincipal("user:rita", "Rita", Org, [Role.Reader], []);
        await Assert.ThrowsAsync<AxiomException>(() => _service.ProposeAsync(reader, Proposal(), default));

        var huge = Proposal(statement: new string('x', CandidateService.MaxAgentStatementLength + 1));
        Assert.Equal(ErrorKind.Validation, (await Assert.ThrowsAsync<AxiomException>(() => _service.ProposeAsync(_agent, huge, default))).Kind);

        var wide = Proposal(scope: new() { ["repositories"] = [.. Enumerable.Range(0, CandidateService.MaxAgentScopeValues + 1).Select(i => "r" + i)] });
        Assert.Equal(ErrorKind.Validation, (await Assert.ThrowsAsync<AxiomException>(() => _service.ProposeAsync(_agent, wide, default))).Kind);

        var noEvidence = Proposal() with { EvidenceLocator = " " };
        Assert.Equal(ErrorKind.Validation, (await Assert.ThrowsAsync<AxiomException>(() => _service.ProposeAsync(_agent, noEvidence, default))).Kind);

        var exception = Proposal() with { Kind = RecordKind.Exception };
        Assert.Equal(ErrorKind.Validation, (await Assert.ThrowsAsync<AxiomException>(() => _service.ProposeAsync(_agent, exception, default))).Kind);
        Assert.Empty(_store.Items);
    }

    [Fact]
    public async Task An_approver_of_the_suggested_team_approves_once_and_the_draft_round_trips_through_the_parser()
    {
        var id = await ProposedAsync();

        var dto = await _service.DecideAsync(_approver, id, approve: true, Comment, default);

        Assert.Equal("Approved", dto.State);
        Assert.False(dto.Authoritative);
        Assert.Equal("user:ada", dto.DecidedBy);
        Assert.Equal(Comment, dto.DecisionComment);
        Assert.Equal(_time.GetUtcNow(), dto.DecidedAt);
        Assert.Contains(_store.Events, e => e.EventType == EventTypes.CandidateDecided);

        // Approving waives and changes nothing: the draft is still a proposed record the real parser accepts.
        var parsed = new GovernanceRecordParser().Parse(dto.DraftRecordPath, dto.DraftRecord);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Issues.Select(i => i.Message)));
        Assert.Equal(LifecycleStatus.Proposed, parsed.Record!.Status);
        var evidence = Assert.Single(parsed.Record.Evidence);
        Assert.Equal(CandidateService.AgentProposalMethod, evidence.ExtractionMethod);
        Assert.Equal("platform-architecture", evidence.SuggestedOwner);
    }

    [Fact]
    public async Task A_candidate_is_decided_only_once()
    {
        var id = await ProposedAsync();
        await _service.DecideAsync(_approver, id, approve: false, Comment, default);

        var second = await Assert.ThrowsAsync<AxiomException>(() => _service.DecideAsync(_admin, id, approve: true, Comment, default));
        Assert.Equal(ErrorKind.Conflict, second.Kind);
        Assert.Equal(CandidateState.Rejected, _store.Items[0].State);
        Assert.Single(_store.Events, e => e.EventType == EventTypes.CandidateDecided);
    }

    [Fact]
    public async Task A_decision_that_loses_the_race_is_a_conflict()
    {
        var id = await ProposedAsync();
        var racing = new RacingStore(_store);
        var service = new CandidateService(racing, _time);

        var error = await Assert.ThrowsAsync<AxiomException>(() => service.DecideAsync(_approver, id, approve: true, Comment, default));
        Assert.Equal(ErrorKind.Conflict, error.Kind);
    }

    [Fact]
    public async Task The_proposer_cannot_decide_their_own_candidate_even_as_an_admin()
    {
        var owner = new AxiomPrincipal("user:dora", "Dora", Org, [Role.DecisionOwner], []);
        var id = await ProposedAsync(by: owner);

        var error = await Assert.ThrowsAsync<AxiomException>(() => _service.DecideAsync(owner, id, approve: true, Comment, default));
        Assert.Equal(ErrorKind.Forbidden, error.Kind);
        Assert.Equal(CandidateState.Proposed, _store.Items[0].State);

        var ownerSelf = new AxiomPrincipal("user:ada", "Ada", Org, [Role.Approver, Role.Contributor], ["platform-architecture"]);
        var id2 = (await _service.ProposeAsync(ownerSelf, Proposal("Another statement."), default)).Candidate.Id;
        Assert.Equal(ErrorKind.Forbidden, (await Assert.ThrowsAsync<AxiomException>(() => _service.DecideAsync(ownerSelf, id2, true, Comment, default))).Kind);
    }

    [Fact]
    public async Task Only_owner_approvers_decision_owners_and_admins_decide()
    {
        var id = await ProposedAsync();
        var otherTeamApprover = new AxiomPrincipal("user:otto", "Otto", Org, [Role.Approver], ["payments"]);
        var teamContributor = new AxiomPrincipal("user:cora", "Cora", Org, [Role.Contributor], ["platform-architecture"]);
        var reader = new AxiomPrincipal("user:rita", "Rita", Org, [Role.Reader], ["platform-architecture"]);
        var foreign = new AxiomPrincipal("user:eve", "Eve", "globex", [Role.PlatformAdmin], []);

        foreach (var denied in new[] { otherTeamApprover, teamContributor, reader })
        {
            Assert.Equal(ErrorKind.Forbidden, (await Assert.ThrowsAsync<AxiomException>(() => _service.DecideAsync(denied, id, true, Comment, default))).Kind);
        }

        Assert.Equal(ErrorKind.NotFound, (await Assert.ThrowsAsync<AxiomException>(() => _service.DecideAsync(foreign, id, true, Comment, default))).Kind);
        Assert.Equal(CandidateState.Proposed, _store.Items[0].State);

        var decisionOwner = new AxiomPrincipal("user:dora", "Dora", Org, [Role.DecisionOwner], []);
        Assert.Equal("Approved", (await _service.DecideAsync(decisionOwner, id, true, Comment, default)).State);
    }

    [Fact]
    public async Task A_candidate_without_an_owner_is_decided_only_by_a_decision_owner_or_admin()
    {
        var id = await ProposedAsync(owner: null);

        Assert.Equal(ErrorKind.Forbidden, (await Assert.ThrowsAsync<AxiomException>(() => _service.DecideAsync(_approver, id, true, Comment, default))).Kind);
        Assert.Equal("Rejected", (await _service.DecideAsync(_admin, id, false, Comment, default)).State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ok")]
    public async Task A_comment_is_required(string? comment)
    {
        var id = await ProposedAsync();

        var error = await Assert.ThrowsAsync<AxiomException>(() => _service.DecideAsync(_approver, id, true, comment, default));
        Assert.Equal(ErrorKind.Validation, error.Kind);
        Assert.Equal(CandidateState.Proposed, _store.Items[0].State);
    }

    [Fact]
    public async Task The_review_queue_lists_pending_first_and_filters_by_owner()
    {
        await _service.ProposeAsync(_agent, Proposal("First statement."), default);
        await _service.ProposeAsync(_agent, Proposal("Second statement.", owner: "payments"), default);
        await _service.ProposeAsync(_agent, Proposal("Third statement."), default);
        await _service.DecideAsync(_admin, _store.Items[0].Id, true, Comment, default);

        var (all, total) = await _service.ListAsync(_approver, new CandidateListQuery(null, null, null, 0, 50), default);
        Assert.Equal(3, total);
        Assert.Equal(["Proposed", "Proposed", "Approved"], all.Select(c => c.State).ToArray());

        var (mine, _) = await _service.ListAsync(_approver, new CandidateListQuery(CandidateState.Proposed, null, "platform-architecture", 0, 50), default);
        Assert.Equal("Third statement.", Assert.Single(mine).Statement);
    }

    [Fact]
    public async Task Approved_candidates_are_marked_merged_only_when_the_snapshot_holds_an_accepted_record_with_their_excerpt()
    {
        var snapshots = new FakeSnapshots();
        var reconciler = new CandidateMergeReconciler(_store, snapshots, _time);
        var id = await ProposedAsync();
        var proposed = await ProposedAsync();
        Assert.Equal(id, proposed);
        await _service.DecideAsync(_approver, id, true, Comment, default);
        var dto = await _service.GetAsync(_approver, id, default);
        var record = new GovernanceRecordParser().Parse(dto.DraftRecordPath, dto.DraftRecord).Record!;

        // Not in the snapshot yet, and a merely proposed record does not count.
        snapshots.Publish(Org);
        Assert.Empty(await reconciler.ReconcileAsync(Org, default));
        snapshots.Publish(Org, record with { Id = "ARCH-100" });
        Assert.Empty(await reconciler.ReconcileAsync(Org, default));
        Assert.Equal(CandidateState.Approved, _store.Items[0].State);

        snapshots.Publish(Org, record with { Id = "ARCH-100", Status = LifecycleStatus.Accepted });
        Assert.Equal([id], await reconciler.ReconcileAsync(Org, default));
        Assert.Equal(CandidateState.MergedAsRecord, _store.Items[0].State);
        Assert.Equal("ARCH-100", _store.Items[0].MergedRecordId);
        Assert.Contains(_store.Events, e => e.EventType == EventTypes.CandidateMerged);
        Assert.Empty(await reconciler.ReconcileAsync(Org, default));
    }

    [Fact]
    public async Task A_proposed_candidate_is_never_merged_by_reconciliation()
    {
        var snapshots = new FakeSnapshots();
        var id = await ProposedAsync();
        var record = new GovernanceRecordParser().Parse(CandidateRecordDraft.PathFor(_store.Items[0]), CandidateRecordDraft.Render(_store.Items[0])).Record!;
        snapshots.Publish(Org, record with { Status = LifecycleStatus.Accepted });

        Assert.Empty(await new CandidateMergeReconciler(_store, snapshots, _time).ReconcileAsync(Org, default));
        Assert.Equal(CandidateState.Proposed, _store.Items.Single(c => c.Id == id).State);
    }

    /// <summary>Reports a stale read: another reviewer decided between the read and the write.</summary>
    private sealed class RacingStore(InMemoryCandidateStore inner) : ICandidateStore
    {
        public Task<Candidate?> FindAsync(string organizationId, string candidateId, CancellationToken cancellationToken) => inner.FindAsync(organizationId, candidateId, cancellationToken);

        public Task<CandidateAddResult> AddAsync(Candidate candidate, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken) => inner.AddAsync(candidate, events, cancellationToken);

        public Task<bool> UpdateAsync(Candidate updated, CandidateState expected, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<(System.Collections.Immutable.ImmutableArray<Candidate> Items, int Total)> ListAsync(string organizationId, CandidateListQuery query, CancellationToken cancellationToken) =>
            inner.ListAsync(organizationId, query, cancellationToken);
    }
}
