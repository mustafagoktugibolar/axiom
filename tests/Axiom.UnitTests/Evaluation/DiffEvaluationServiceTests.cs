using System.Collections.Immutable;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Application.Policy;
using Axiom.Application.Review;
using Axiom.Domain.Audit;
using Axiom.Domain.Catalog;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;
using Axiom.Domain.Review;
using Axiom.UnitTests.Governance;
using Axiom.UnitTests.Policy;
using Microsoft.Extensions.Time.Testing;

namespace Axiom.UnitTests.Evaluation;

public class DiffEvaluationServiceTests
{
    private const string Org = "acme";
    private const string BaseSha = "aaaaaaa1";
    private const string HeadSha = "bbbbbbb2";

    private readonly FakeSnapshots _snapshots = new();
    private readonly FakeGraph _graph = new();
    private readonly InMemoryEvaluationStore _store = new();
    private readonly InMemoryReviews _reviews = new();
    private readonly FakeScm _scm = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly DiffEvaluationService _service;
    private readonly AxiomPrincipal _developer = Principals.With(Org, Role.Contributor);

    public DiffEvaluationServiceTests()
    {
        _service = new DiffEvaluationService(
            new EvaluationPipeline(_snapshots, new ChangeScopeResolver(_graph), _graph, _store, _time),
            _scm,
            new PolicyEngine(PolicyRuleCatalog.BuiltIn),
            PolicyRuleCatalog.BuiltIn,
            _store,
            _reviews);
        _graph.Bind(Topology("component:gui/api-gateway"));
        PublishGeneratedCodeRule(EnforcementLevel.Block);
    }

    private static RepositoryTopology Topology(string component) =>
        new(EntityRef.Parse("repo:gateway"), [EntityRef.Parse(component)], [EntityRef.Parse("system:gui-platform")], [], [], [], [],
            [EntityRef.Parse("team:platform")], [], []);

    private void PublishGeneratedCodeRule(EnforcementLevel mode) => _snapshots.Publish(Org,
        TestRecords.Decision("ARCH-100", scope: Scope.Of((ScopeDimension.Repository, ["gateway"])),
            rules: [PolicyTestKit.Bind("forbidden-path-change", mode, ("paths", "src/Generated/**"))]));

    private static DiffValidationRequest Request(string? design = null, string? pullRequest = null) =>
        new(Org, "gateway", BaseSha, HeadSha, "feature/x", pullRequest, design, "kiro", "s1");

    private Task<StoredEvaluation> RunAsync(DiffValidationRequest? request = null) => _service.ExecuteAsync(_developer, request ?? Request(), default);

    private async Task<string> DesignAsync(
        Verdict verdict, bool significant = false, string[]? classes = null, string component = "component:gui/api-gateway", string id = "ev_design")
    {
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
            DesignId = "DESIGN-1",
            DesignHash = "dh-" + id,
            ResolvedScope = new ResolvedScopeView(
                ImmutableSortedDictionary<string, ImmutableArray<string>>.Empty
                    .Add("component", [component]).Add("repository", ["repo:gateway"]).Add("system", ["system:gui-platform"]),
                []),
            Significance = new SignificanceAssessment(significant, [], [.. classes ?? []]),
            Findings = verdict >= Verdict.RequireReview
                ? [new Finding { Code = "X", Severity = verdict == Verdict.Block ? EnforcementLevel.Block : EnforcementLevel.RequireReview, Source = FindingSource.Policy, Message = "m" }]
                : [],
            RequiredReviewers = ["platform-architecture"],
            Verdict = verdict,
        };
        await _store.AppendAsync(evaluation, [], default);
        return id;
    }

    private void Changes(params ChangedFile[] files) => _scm.Diff = new ScmDiff([.. files], InMemoryRepositoryView.FromChanges(files), false, files.Length);

    private static ChangedFile Source(string path = "src/Program.cs") => PolicyTestKit.Added(path, "class Program;");

    private static string[] Codes(StoredEvaluation stored) => [.. stored.Evaluation.Findings.Select(f => f.Code)];

    [Fact]
    public async Task A_clean_change_is_allowed_and_the_bound_rule_is_recorded_as_a_passed_run()
    {
        Changes(Source());

        var stored = await RunAsync();

        Assert.Equal(Verdict.Allow, stored.Evaluation.Verdict);
        Assert.Equal(EvaluationStage.Diff, stored.Evaluation.Stage);
        Assert.Equal((BaseSha, HeadSha), (stored.Evaluation.Scm.BaseSha, stored.Evaluation.Scm.CommitSha));
        var run = Assert.Single(stored.Evaluation.PolicyRuns);
        Assert.Equal(("forbidden-path-change", "ARCH-100", PolicyOutcome.Passed), (run.RuleId, run.RecordId, run.Outcome));
        Assert.Equal(["src/Program.cs"], stored.Evaluation.ResolvedScope.Dimensions["path"].AsEnumerable());
        Assert.True(ReceiptFactory.Verify(stored.Receipt));
    }

    [Fact]
    public async Task A_violated_block_rule_blocks_and_names_the_record_rule_and_file()
    {
        Changes(Source("src/Generated/Client.cs"));

        var stored = await RunAsync();

        Assert.Equal(Verdict.Block, stored.Evaluation.Verdict);
        var finding = Assert.Single(stored.Evaluation.Findings, f => f.Code == PolicyFindingCodes.Violation);
        Assert.Equal(("src/Generated/Client.cs", "forbidden-path-change", EnforcementLevel.Block), (finding.Path, finding.RuleId, finding.Severity));
        Assert.Equal(["ARCH-100"], finding.GovernanceIds.AsEnumerable());
        Assert.Equal(PolicyOutcome.Violated, Assert.Single(stored.Evaluation.PolicyRuns).Outcome);
    }

    [Fact]
    public async Task An_active_exception_waives_the_violation_but_keeps_it_visible()
    {
        _snapshots.Publish(Org,
            TestRecords.Decision("ARCH-100", scope: Scope.Of((ScopeDimension.Repository, ["gateway"])),
                rules: [PolicyTestKit.Bind("forbidden-path-change", EnforcementLevel.Block, ("paths", "src/Generated/**"))]),
            TestRecords.Exception("EXC-1", ["ARCH-100"], Scope.Of((ScopeDimension.Repository, ["gateway"]), (ScopeDimension.Path, ["src/Generated/**"])),
                _time.GetUtcNow().AddDays(-1), _time.GetUtcNow().AddDays(10)));
        Changes(Source("src/Generated/Client.cs"));

        var stored = await RunAsync();

        var finding = Assert.Single(stored.Evaluation.Findings, f => f.Code == PolicyFindingCodes.Violation);
        Assert.Equal("EXC-1", finding.WaivedBy);
        Assert.Equal(Verdict.Allow, stored.Evaluation.Verdict);
    }

    [Fact]
    public async Task An_identical_request_returns_the_stored_evaluation()
    {
        Changes(Source());

        var first = await RunAsync();
        var second = await RunAsync();

        Assert.Equal(first.Evaluation.Id, second.Evaluation.Id);
        Assert.Single(_store.Items);
    }

    [Fact]
    public async Task A_significant_diff_without_a_design_requires_review()
    {
        Changes(PolicyTestKit.Added("contracts/openapi.yaml", "openapi: 3.1.0"));

        var stored = await RunAsync();

        Assert.Equal(Verdict.RequireReview, stored.Evaluation.Verdict);
        var finding = Assert.Single(stored.Evaluation.Findings, f => f.Code == EvaluationPipeline.DesignRequired);
        Assert.Equal(EnforcementLevel.RequireReview, finding.Severity);
        Assert.Contains(DiffEvaluationService.RequestDesign, stored.Evaluation.RequiredActions);
    }

    [Fact]
    public async Task A_diff_inside_an_approved_design_is_allowed_and_inherits_its_hash()
    {
        var design = await DesignAsync(Verdict.Allow, significant: true, classes: [ChangeClasses.PublicContract]);
        Changes(PolicyTestKit.Added("contracts/openapi.yaml", "openapi: 3.1.0"));

        var stored = await RunAsync(Request(design));

        Assert.Equal(Verdict.Allow, stored.Evaluation.Verdict);
        Assert.Equal(design, stored.Evaluation.ParentEvaluationId);
        Assert.Equal("dh-" + design, stored.Evaluation.DesignHash);
        Assert.Equal("DESIGN-1", stored.Evaluation.DesignId);
    }

    [Fact]
    public async Task A_significant_diff_against_a_design_classified_not_significant_is_scope_expansion()
    {
        var design = await DesignAsync(Verdict.Allow, significant: false);
        Changes(PolicyTestKit.Added("deploy/helm/values.yaml", "replicas: 3"));

        var stored = await RunAsync(Request(design));

        Assert.Equal(Verdict.RequireReview, stored.Evaluation.Verdict);
        var finding = Assert.Single(stored.Evaluation.Findings, f => f.Code == DiffFindingCodes.ScopeExpansion);
        Assert.Contains("classified as not significant", finding.Message, StringComparison.Ordinal);
        Assert.Contains(DiffEvaluationService.RevalidateDesign, stored.Evaluation.RequiredActions);
    }

    [Fact]
    public async Task A_change_class_the_design_did_not_cover_is_scope_expansion()
    {
        var design = await DesignAsync(Verdict.Allow, significant: true, classes: [ChangeClasses.PublicContract]);
        Changes(PolicyTestKit.Added("contracts/openapi.yaml", "openapi: 3.1.0"), PolicyTestKit.Added("deploy/helm/values.yaml", "replicas: 3"));

        var stored = await RunAsync(Request(design));

        var finding = Assert.Single(stored.Evaluation.Findings, f => f.Code == DiffFindingCodes.ScopeExpansion);
        Assert.Contains(ChangeClasses.Infrastructure, finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ChangeClasses.PublicContract, finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Topology_the_design_did_not_know_is_scope_expansion()
    {
        var design = await DesignAsync(Verdict.Allow, component: "component:gui/other");
        Changes(Source());

        var stored = await RunAsync(Request(design));

        var finding = Assert.Single(stored.Evaluation.Findings, f => f.Code == DiffFindingCodes.ScopeExpansion);
        Assert.Contains("component: component:gui/api-gateway", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reviewer_ruling_that_the_change_is_not_significant_lifts_the_design_requirement()
    {
        var design = await DesignAsync(Verdict.Allow, significant: false);
        _reviews.Decisions.Add(new ReviewDecision("rd_1", Org, design, ReviewKind.ClassificationOverride, ReviewOutcome.Approved, "dh-" + design, null, "user:bob", "Not an architectural change.", _time.GetUtcNow()));
        Changes(PolicyTestKit.Added("deploy/helm/values.yaml", "replicas: 3"));

        var stored = await RunAsync(Request(design));

        Assert.DoesNotContain(DiffFindingCodes.ScopeExpansion, Codes(stored));
    }

    [Fact]
    public async Task A_design_awaiting_review_holds_the_diff_until_it_is_approved()
    {
        var design = await DesignAsync(Verdict.RequireReview);
        Changes(Source());

        var pending = await RunAsync(Request(design));
        _reviews.Decisions.Add(new ReviewDecision("rd_1", Org, design, ReviewKind.EvaluationReview, ReviewOutcome.Approved, "dh-" + design, null, "user:bob", "Approved after discussion.", _time.GetUtcNow()));
        var approved = await RunAsync(Request(design));

        Assert.Equal(Verdict.RequireReview, pending.Evaluation.Verdict);
        Assert.Contains(DiffFindingCodes.DesignApprovalPending, Codes(pending));
        Assert.Contains("platform-architecture", pending.Evaluation.RequiredActions.Single(a => a.Contains("review", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Equal(Verdict.Allow, approved.Evaluation.Verdict);
        Assert.NotEqual(pending.Evaluation.Id, approved.Evaluation.Id);
    }

    [Fact]
    public async Task A_rejected_or_blocked_design_blocks_the_diff()
    {
        var rejected = await DesignAsync(Verdict.RequireReview, id: "ev_rejected");
        _reviews.Decisions.Add(new ReviewDecision("rd_1", Org, rejected, ReviewKind.EvaluationReview, ReviewOutcome.Rejected, "dh-" + rejected, null, "user:bob", "This violates the gateway rules.", _time.GetUtcNow()));
        var blocked = await DesignAsync(Verdict.Block, id: "ev_blocked");
        Changes(Source());

        var first = await RunAsync(Request(rejected));
        var second = await RunAsync(Request(blocked));

        Assert.Contains(DiffFindingCodes.DesignRejected, Codes(first));
        Assert.Contains(DiffFindingCodes.DesignBlocked, Codes(second));
        Assert.All([first, second], s => Assert.Equal(Verdict.Block, s.Evaluation.Verdict));
    }

    [Fact]
    public async Task A_truncated_diff_is_never_treated_as_complete()
    {
        _scm.Diff = new ScmDiff([Source()], InMemoryRepositoryView.Empty, true, 5_000);

        var stored = await RunAsync();

        Assert.Equal(Verdict.RequireReview, stored.Evaluation.Verdict);
        Assert.Contains("5000", Assert.Single(stored.Evaluation.Findings, f => f.Code == DiffFindingCodes.DiffTruncated).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_pull_request_gate_is_a_separate_stage_that_needs_the_pull_request()
    {
        Changes(Source());

        var stored = await _service.ExecutePullRequestAsync(_developer, Request(pullRequest: "42"), default);
        var diff = await RunAsync();

        Assert.Equal((EvaluationStage.PullRequest, "42"), (stored.Evaluation.Stage, stored.Evaluation.Scm.PullRequest));
        Assert.NotEqual(diff.Evaluation.Id, stored.Evaluation.Id);
        await Assert.ThrowsAsync<AxiomException>(() => _service.ExecutePullRequestAsync(_developer, Request(), default));
    }

    [Fact]
    public async Task The_design_reference_must_exist_and_be_a_design_evaluation()
    {
        Changes(Source());
        var preflight = new EvaluationRecord
        {
            Id = "ev_pre",
            OrganizationId = Org,
            Stage = EvaluationStage.Preflight,
            CreatedAt = _time.GetUtcNow(),
            Actor = new ActorIdentity("user:alice", null, null, null),
            Scm = new ScmCoordinates("repo:gateway", null, null, null, null),
            RequestFingerprint = "fp",
            SnapshotId = "gs_1",
            SourceCommit = "c1",
            SnapshotPublishedAt = _time.GetUtcNow(),
            CatalogVersion = "cv_1",
            ResolvedScope = new ResolvedScopeView(ImmutableSortedDictionary<string, ImmutableArray<string>>.Empty, []),
            Significance = new SignificanceAssessment(false, [], []),
            Verdict = Verdict.Allow,
        };
        await _store.AppendAsync(preflight, [], default);

        Assert.Equal(ErrorKind.NotFound, (await Assert.ThrowsAsync<AxiomException>(() => RunAsync(Request("ev_missing")))).Kind);
        Assert.Equal(ErrorKind.Validation, (await Assert.ThrowsAsync<AxiomException>(() => RunAsync(Request("ev_pre")))).Kind);
    }

    [Fact]
    public async Task Callers_need_the_evaluate_right_and_a_well_formed_request()
    {
        Changes(Source());

        await Assert.ThrowsAsync<AxiomException>(() => _service.ExecuteAsync(Principals.With(Org, Role.Reader), Request(), default));
        await Assert.ThrowsAsync<AxiomException>(() => RunAsync(Request() with { HeadSha = "not a sha" }));
        await Assert.ThrowsAsync<AxiomException>(() => RunAsync(Request() with { Organization = "other" }));
    }

    [Fact]
    public async Task A_source_control_failure_is_an_error_not_a_verdict()
    {
        _scm.Failure = new AxiomException(ErrorKind.Unavailable, ErrorCodes.ScmUnavailable, "down", retryable: true);

        var error = await Assert.ThrowsAsync<AxiomException>(() => RunAsync());

        Assert.Equal(ErrorCodes.ScmUnavailable, error.Code);
        Assert.Empty(_store.Items);
    }

    private sealed class FakeScm : IScmDiffSource
    {
        public ScmDiff Diff { get; set; } = new([], InMemoryRepositoryView.Empty, false, 0);

        public Exception? Failure { get; set; }

        public Task<ScmDiff> GetDiffAsync(ScmDiffRequest request, CancellationToken cancellationToken) =>
            Failure is null ? Task.FromResult(Diff) : Task.FromException<ScmDiff>(Failure);
    }

    private sealed class InMemoryReviews : IReviewStore
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
}
