using System.Collections.Immutable;
using Axiom.Application.Candidates;
using Axiom.Application.Common;
using Axiom.Domain.Governance;
using Axiom.Domain.Resolution;
using Axiom.Infrastructure.Governance.Parsing;
using Microsoft.Extensions.Time.Testing;

namespace Axiom.UnitTests.Candidates;

public class CandidateServiceTests
{
    private const string Org = "acme";
    private readonly InMemoryCandidateStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly CandidateService _service;
    private readonly AxiomPrincipal _admin = new("user:admin", "Admin", Org, [Role.PlatformAdmin], []);
    private readonly AxiomPrincipal _reader = new("user:rita", "Rita", Org, [Role.Reader], []);

    public CandidateServiceTests() => _service = new CandidateService(_store, _time);

    private static CandidateInput Input(
        string hash = "h1", RecordKind kind = RecordKind.Decision, string? owner = "platform-architecture", double confidence = 0.85, string? statement = "Use PostgreSQL for all services.",
        Dictionary<string, string[]>? hints = null) =>
        new("gateway:docs/adr/0001-db.md@abc", hash, "adr-markdown/nygard@1", confidence, owner, kind, "Use PostgreSQL", statement, "## Context\nWe need a database.",
            hints ?? new() { ["repositories"] = ["gateway"] });

    [Fact]
    public async Task Ingest_stores_a_proposed_non_authoritative_candidate_and_queues_an_event()
    {
        var result = await _service.IngestAsync(_admin, [Input()], default);

        Assert.Equal(1, result.Created);
        var stored = Assert.Single(_store.Items);
        Assert.Equal(CandidateState.Proposed, stored.State);
        Assert.False(stored.IsAuthoritative);
        Assert.Equal("user:admin", stored.CreatedBy);
        Assert.Contains(_store.Events, e => e.EventType == EventTypes.CandidateDecisionCreated && e.OrganizationId == Org);
    }

    [Fact]
    public async Task The_draft_is_a_proposed_record_the_governance_parser_accepts_with_its_evidence()
    {
        await _service.IngestAsync(_admin, [Input()], default);
        var dto = await _service.GetAsync(_reader, _store.Items[0].Id, default);

        Assert.False(dto.Authoritative);
        Assert.Equal(["platform-architecture"], dto.Owners.AsEnumerable());
        Assert.StartsWith("governance/decisions/CAND-", dto.DraftRecordPath, StringComparison.Ordinal);
        var parsed = new GovernanceRecordParser().Parse(dto.DraftRecordPath, dto.DraftRecord);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Issues.Select(i => i.Message)));
        var record = parsed.Record!;
        Assert.Equal(LifecycleStatus.Proposed, record.Status);
        Assert.Contains("candidate", record.Tags);
        var evidence = Assert.Single(record.Evidence);
        Assert.Equal("h1", evidence.ExcerptHash);
        Assert.Equal("adr-markdown/nygard@1", evidence.ExtractionMethod);
        Assert.Equal("platform-architecture", evidence.SuggestedOwner);
        Assert.Equal(["gateway"], record.Scope[ScopeDimension.Repository].AsEnumerable());
        Assert.Contains("We need a database.", record.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_candidate_without_a_suggested_owner_is_flagged_unassigned()
    {
        await _service.IngestAsync(_admin, [Input(owner: null)], default);
        var dto = await _service.GetAsync(_reader, _store.Items[0].Id, default);

        Assert.Equal([CandidateRecordDraft.UnassignedOwner], dto.Owners.AsEnumerable());
        Assert.True(new GovernanceRecordParser().Parse(dto.DraftRecordPath, dto.DraftRecord).IsValid);
    }

    [Fact]
    public async Task Ingesting_the_same_excerpt_again_is_a_no_op_even_after_a_decision()
    {
        await _service.IngestAsync(_admin, [Input()], default);
        _store.Items[0] = _store.Items[0].Reject("user:bob", "Out of date.", _time.GetUtcNow());

        var again = await _service.IngestAsync(_admin, [Input(), Input()], default);

        Assert.Equal(0, again.Created);
        Assert.Equal(2, again.Existing);
        Assert.Single(_store.Items);
        Assert.Equal(CandidateState.Rejected, _store.Items[0].State);
        Assert.Single(_store.Events);
    }

    [Fact]
    public async Task Candidates_are_isolated_per_organization()
    {
        await _service.IngestAsync(_admin, [Input()], default);
        var other = new AxiomPrincipal("user:eve", "Eve", "globex", [Role.Reader], []);

        var error = await Assert.ThrowsAsync<AxiomException>(() => _service.GetAsync(other, _store.Items[0].Id, default));
        Assert.Equal(ErrorKind.NotFound, error.Kind);
        Assert.Empty((await _service.ListAsync(other, new CandidateListQuery(null, null, null, 0, 50), default)).Items);
    }

    [Theory]
    [InlineData("confidence")]
    [InlineData("exception")]
    [InlineData("statement")]
    [InlineData("dimension")]
    [InlineData("hash")]
    public async Task Invalid_input_is_rejected_and_nothing_is_stored(string case_)
    {
        var input = case_ switch
        {
            "confidence" => Input(confidence: 1.5),
            "exception" => Input(kind: RecordKind.Exception),
            "statement" => Input(statement: "  "),
            "dimension" => Input(hints: new() { ["planets"] = ["mars"] }),
            _ => Input(hash: " "),
        };

        var error = await Assert.ThrowsAsync<AxiomException>(() => _service.IngestAsync(_admin, [input], default));
        Assert.Equal(ErrorKind.Validation, error.Kind);
        Assert.Empty(_store.Items);
    }

    [Fact]
    public async Task Only_a_source_manager_ingests_but_any_reader_reads()
    {
        var contributor = new AxiomPrincipal("user:carl", "Carl", Org, [Role.Contributor], []);
        await Assert.ThrowsAsync<AxiomException>(() => _service.IngestAsync(_reader, [Input()], default));
        await Assert.ThrowsAsync<AxiomException>(() => _service.IngestAsync(contributor, [Input()], default));

        await _service.IngestAsync(_admin, [Input()], default);
        Assert.Single((await _service.ListAsync(_reader, new CandidateListQuery(null, null, null, 0, 50), default)).Items);

        var nobody = new AxiomPrincipal("user:x", null, Org, [], []);
        await Assert.ThrowsAsync<AxiomException>(() => _service.ListAsync(nobody, new CandidateListQuery(null, null, null, 0, 50), default));
        await Assert.ThrowsAsync<AxiomException>(() => _service.GetAsync(nobody, _store.Items[0].Id, default));
    }

    [Fact]
    public async Task List_filters_by_state_kind_and_owner_and_is_bounded()
    {
        await _service.IngestAsync(_admin, [Input("a"), Input("b", owner: "payments"), Input("c", kind: RecordKind.Standard)], default);
        _store.Items[0] = _store.Items[0].Approve("user:bob", "Looks right.", _time.GetUtcNow());

        Assert.Equal(1, (await _service.ListAsync(_reader, new CandidateListQuery(CandidateState.Approved, null, null, 0, 50), default)).Total);
        Assert.Equal(1, (await _service.ListAsync(_reader, new CandidateListQuery(null, RecordKind.Standard, null, 0, 50), default)).Total);
        Assert.Equal(1, (await _service.ListAsync(_reader, new CandidateListQuery(null, null, "payments", 0, 50), default)).Total);

        var (items, total) = await _service.ListAsync(_reader, new CandidateListQuery(null, null, null, -5, 0), default);
        Assert.Equal(3, total);
        Assert.Single(items);
    }

    [Fact]
    public void Candidate_ids_derive_from_the_excerpt_and_the_organization()
    {
        Assert.Equal(Candidate.IdFor("acme", "h"), Candidate.IdFor("acme", "h"));
        Assert.NotEqual(Candidate.IdFor("acme", "h"), Candidate.IdFor("globex", "h"));
        Assert.NotEqual(Candidate.IdFor("acme", "h"), Candidate.IdFor("acme", "i"));
    }

    [Fact]
    public async Task States_follow_proposed_to_approved_or_rejected_and_approved_to_merged_only()
    {
        await _service.IngestAsync(_admin, [Input()], default);
        var proposed = _store.Items[0];
        var at = _time.GetUtcNow();

        var approved = proposed.Approve("user:bob", "Yes.", at);
        Assert.Equal(CandidateState.Approved, approved.State);
        Assert.Equal("user:bob", approved.DecidedBy);
        var merged = approved.MarkMerged("ARCH-100");
        Assert.Equal(CandidateState.MergedAsRecord, merged.State);
        Assert.Equal("ARCH-100", merged.MergedRecordId);

        Assert.Throws<InvalidOperationException>(() => proposed.MarkMerged("ARCH-100"));
        Assert.Throws<InvalidOperationException>(() => approved.Reject("user:bob", "No.", at));
        Assert.Throws<InvalidOperationException>(() => merged.Approve("user:bob", "Again.", at));
        Assert.Throws<InvalidOperationException>(() => proposed.Reject("user:bob", "No.", at).Approve("user:bob", "Yes.", at));
    }

    [Theory]
    [InlineData(CandidateState.Proposed)]
    [InlineData(CandidateState.Approved)]
    [InlineData(CandidateState.Rejected)]
    [InlineData(CandidateState.MergedAsRecord)]
    public async Task A_candidate_never_governs_in_any_state(CandidateState state)
    {
        await _service.IngestAsync(_admin, [Input()], default);
        var candidate = _store.Items[0] with { State = state };

        Assert.False(candidate.IsAuthoritative);
        Assert.False(CandidateService.ToDto(candidate).Authoritative);
        // Even the approved draft renders as a proposed record: only the merged, accepted record can govern.
        var parsed = new GovernanceRecordParser().Parse(CandidateRecordDraft.PathFor(candidate), CandidateRecordDraft.Render(candidate));
        Assert.Equal(LifecycleStatus.Proposed, parsed.Record!.Status);
    }

    [Fact]
    public async Task A_candidate_draft_is_never_applicable_to_a_change_it_matches()
    {
        await _service.IngestAsync(_admin, [Input(hints: new() { ["repositories"] = ["gateway"] })], default);
        var draft = CandidateRecordDraft.ToRecord(_store.Items[0]);
        var revision = new RecordRevision(draft, new SourceProvenance("governance", "abc", "governance/decisions/x.md", "blob"), 1);

        var result = DecisionResolver.Resolve(new ResolutionInput(
            [revision], EvaluationScope.Empty.With(ScopeDimension.Repository, "gateway"), _time.GetUtcNow(), HardGate: true));

        Assert.Empty(result.Applicable);
        Assert.False(draft.IsAuthoritativeOn(DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime)));
    }
}
