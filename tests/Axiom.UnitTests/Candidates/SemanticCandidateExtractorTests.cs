using System.Collections.Immutable;
using Axiom.Application.Candidates;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Application.Semantic;
using Axiom.Domain.Catalog;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Axiom.UnitTests.Candidates;

public class SemanticCandidateExtractorTests
{
    private const string Org = "acme";
    private readonly InMemoryCandidateStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly AxiomPrincipal _contributor = new("user:carol", "Carol", Org, [Role.Contributor], []);

    private static readonly ChangedFile[] Files =
    [
        new("src/Db.cs", ChangeKind.Modified, null, null, null, [new AddedLine(12, "services.AddDbContext(o => o.UseNpgsql(\"password=hunter2\"));"), new AddedLine(13, "other")]),
    ];

    private static readonly RepositoryTopology Topology = new(
        new EntityRef(EntityKind.Repository, "gateway"), [], [], [], [], [], [],
        [new EntityRef(EntityKind.Team, "zeta"), new EntityRef(EntityKind.Team, "alpha")], [], []);

    private SemanticCandidateExtractor Extractor(bool enabled = true, double min = 0.8, ICandidateStore? store = null) =>
        new(Options.Create(new SemanticOptions { ExtractCandidates = enabled, ExtractionMinConfidence = min }),
            new CandidateService(store ?? _store, _time), NullLogger<SemanticCandidateExtractor>.Instance);

    private static Finding Unrecorded(double confidence = 0.9, string path = "src/Db.cs", int? line = 12, string explanation = "Adds a PostgreSQL datastore.", string code = SemanticFindingCodes.UnrecordedDecision) =>
        new()
        {
            Code = code,
            Severity = EnforcementLevel.RequireReview,
            Source = FindingSource.Semantic,
            Message = SemanticAnalyzer.UnrecordedMessagePrefix + explanation,
            Path = path,
            Line = line,
            Confidence = confidence,
        };

    private EvaluationRecord Evaluation(params Finding[] findings) => new()
    {
        Id = "ev_1",
        OrganizationId = Org,
        Stage = EvaluationStage.Diff,
        CreatedAt = _time.GetUtcNow(),
        Actor = new ActorIdentity("user:carol", null, null, null),
        Scm = new ScmCoordinates("repo:gateway", null, null, null, null),
        RequestFingerprint = "fp",
        SnapshotId = "gs_1",
        SourceCommit = "c1",
        SnapshotPublishedAt = _time.GetUtcNow(),
        CatalogVersion = "cv_1",
        ResolvedScope = new ResolvedScopeView(ImmutableSortedDictionary<string, ImmutableArray<string>>.Empty, []),
        Significance = new SignificanceAssessment(false, [], []),
        Findings = [.. findings],
        Verdict = Verdict.RequireReview,
    };

    [Fact]
    public async Task A_confident_unrecorded_decision_becomes_a_proposed_candidate_with_evidence_and_owner()
    {
        var result = await Extractor().ExtractAsync(_contributor, Evaluation(Unrecorded()), "gateway", Topology, Files, default);

        Assert.Equal(1, result!.Created);
        var candidate = Assert.Single(_store.Items);
        Assert.Equal(CandidateState.Proposed, candidate.State);
        Assert.False(candidate.IsAuthoritative);
        Assert.Equal("semantic/unrecorded-decision@1", candidate.Evidence.ExtractionMethod);
        Assert.Equal("evaluation:ev_1#src/Db.cs:12", candidate.Evidence.SourceLocator);
        Assert.Equal("alpha", candidate.Evidence.SuggestedOwner);
        Assert.Equal(0.9, candidate.Evidence.Confidence);
        Assert.Contains("PostgreSQL", candidate.Draft.Statement, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", candidate.Draft.Rationale, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", candidate.Draft.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_is_off_by_default_and_ignores_low_confidence_other_codes_and_waived_findings()
    {
        Assert.Null(await Extractor(enabled: false).ExtractAsync(_contributor, Evaluation(Unrecorded()), "gateway", Topology, Files, default));
        Assert.False(new SemanticOptions().ExtractCandidates);

        var evaluation = Evaluation(
            Unrecorded(0.79), Unrecorded(code: SemanticFindingCodes.PossibleDuplicateSolution), Unrecorded(path: " "),
            Unrecorded(line: 13) with { WaivedBy = "EXC-1" });
        Assert.Null(await Extractor().ExtractAsync(_contributor, evaluation, "gateway", Topology, Files, default));
        Assert.Empty(_store.Items);
    }

    [Fact]
    public async Task Extraction_is_idempotent_by_excerpt_across_evaluations()
    {
        var extractor = Extractor();
        await extractor.ExtractAsync(_contributor, Evaluation(Unrecorded()), "gateway", Topology, Files, default);
        var again = await extractor.ExtractAsync(_contributor, Evaluation(Unrecorded()) with { Id = "ev_2" }, "gateway", Topology, Files, default);

        Assert.Equal(1, again!.Existing);
        Assert.Single(_store.Items);
    }

    [Fact]
    public void Provider_text_is_sanitized_and_bounded()
    {
        var hostile = "Adds\u0001 a datastore\n" + new string('x', 5_000) + " token=abcd1234";
        var inputs = Extractor().Derive(Evaluation(Unrecorded(explanation: hostile)), "gateway", null, Files);

        var input = Assert.Single(inputs);
        Assert.True(input.Title!.Length <= SemanticCandidateExtractor.MaxTitleChars);
        Assert.True(input.Statement!.Length <= SemanticCandidateExtractor.MaxStatementChars);
        Assert.DoesNotContain(input.Statement, char.IsControl);
        Assert.Null(input.SuggestedOwner);
    }

    [Fact]
    public void A_line_the_packet_did_not_contain_yields_no_excerpt()
    {
        var input = Assert.Single(Extractor().Derive(Evaluation(Unrecorded(line: 99)), "gateway", Topology, Files));

        Assert.DoesNotContain("Excerpt", input.Rationale, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_store_failure_is_swallowed_and_never_surfaces()
    {
        var result = await Extractor(store: new ThrowingStore()).ExtractAsync(_contributor, Evaluation(Unrecorded()), "gateway", Topology, Files, default);

        Assert.Null(result);
    }

    [Fact]
    public async Task A_caller_who_may_not_evaluate_cannot_extract_and_nothing_is_stored()
    {
        var reader = new AxiomPrincipal("user:rita", null, Org, [Role.Reader], []);

        Assert.Null(await Extractor().ExtractAsync(reader, Evaluation(Unrecorded()), "gateway", Topology, Files, default));
        Assert.Empty(_store.Items);
    }

    private sealed class ThrowingStore : ICandidateStore
    {
        public Task<Candidate?> FindAsync(string organizationId, string candidateId, CancellationToken cancellationToken) => throw new InvalidOperationException("down");

        public Task<CandidateAddResult> AddAsync(Candidate candidate, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken) => throw new InvalidOperationException("down");

        public Task<bool> UpdateAsync(Candidate updated, CandidateState expected, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken) => throw new InvalidOperationException("down");

        public Task<(ImmutableArray<Candidate> Items, int Total)> ListAsync(string organizationId, CandidateListQuery query, CancellationToken cancellationToken) => throw new InvalidOperationException("down");
    }
}
