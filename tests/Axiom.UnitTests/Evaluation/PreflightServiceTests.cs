using System.Collections.Immutable;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Domain.Audit;
using Axiom.Domain.Catalog;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Axiom.Domain.Resolution;
using Axiom.Infrastructure.Governance.Parsing;
using Axiom.UnitTests.Governance;
using Microsoft.Extensions.Time.Testing;

namespace Axiom.UnitTests.Evaluation;

public class PreflightServiceTests
{
    private const string Org = "acme";
    private readonly FakeSnapshots _snapshots = new();
    private readonly FakeGraph _graph = new();
    private readonly InMemoryEvaluationStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly PreflightService _preflight;
    private readonly AxiomPrincipal _developer = Principals.With(Org, Role.Contributor);

    public PreflightServiceTests()
    {
        _preflight = new PreflightService(new EvaluationPipeline(_snapshots, new ChangeScopeResolver(_graph), _graph, _store, _time));
        _graph.Bind(new RepositoryTopology(
            EntityRef.Parse("repo:gateway"),
            [EntityRef.Parse("component:gui/api-gateway")],
            [EntityRef.Parse("system:gui-platform")],
            [EntityRef.Parse("domain:gui")],
            [EntityRef.Parse("capability:routing"), EntityRef.Parse("capability:homepage-resolution")],
            [EntityRef.Parse("api:gateway/v1")],
            [],
            [EntityRef.Parse("team:platform")],
            ["dotnet"],
            []));
    }

    private static GovernanceRecord SpecExample(string relative, string path) =>
        new GovernanceRecordParser().Parse(path, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SpecExamples", relative))).Record!;

    private void PublishSpecExamples() => _snapshots.Publish(Org,
        SpecExample("decisions/ARCH-042-gateway-routing.md", "governance/decisions/ARCH-042.md"),
        SpecExample("exceptions/EXC-023-legacy-homepage.yaml", "governance/exceptions/EXC-023.yaml"));

    private static PreflightRequest Request(string task = "Resolve homepage by user role in the gateway", params string[] paths) =>
        new(Org, "gateway", "feature/homepage", task, paths.Length == 0 ? null : paths, null, "kiro", "s1");

    [Fact]
    public async Task Returns_scope_applicable_records_snapshot_and_a_receipt()
    {
        PublishSpecExamples();

        var (evaluation, receipt) = await _preflight.ExecuteAsync(_developer, Request(paths: "src/Homepage/Resolver.cs"), default);

        Assert.Equal(Verdict.Allow, evaluation.Verdict);
        Assert.StartsWith("ev_", evaluation.Id, StringComparison.Ordinal);
        Assert.StartsWith("gs_", evaluation.SnapshotId, StringComparison.Ordinal);
        Assert.Equal(["system:gui-platform"], evaluation.ResolvedScope.Dimensions["system"].AsEnumerable());
        Assert.Equal(["component:gui/api-gateway"], evaluation.ResolvedScope.Dimensions["component"].AsEnumerable());
        var applied = Assert.Single(evaluation.AppliedRecords);
        Assert.Equal(("ARCH-042", 1, Importance.Required), (applied.Id, applied.Revision, applied.Importance));
        Assert.Empty(evaluation.AppliedExceptions);
        Assert.Equal(new RequiredCheck("gateway-no-domain-db-access", "ARCH-042", EnforcementLevel.Block), Assert.Single(evaluation.RequiredChecks));
        Assert.Contains(PreflightService.ValidateDiff, evaluation.RequiredActions);
        Assert.True(ReceiptFactory.Verify(receipt));
        Assert.Equal(evaluation.Id, receipt.EvaluationId);
        Assert.Contains(_store.Events, e => e.EventType == EventTypes.EvaluationCompleted);
    }

    [Fact]
    public async Task Active_exception_is_reported_and_waives_its_target_inside_its_scope()
    {
        PublishSpecExamples();

        var (evaluation, _) = await _preflight.ExecuteAsync(_developer, Request(paths: "src/Legacy/Homepage/Old.cs"), default);

        Assert.Equal("EXC-023", Assert.Single(evaluation.AppliedExceptions).Id);
        Assert.Equal(Importance.Waived, Assert.Single(evaluation.AppliedRecords).Importance);
    }

    [Fact]
    public async Task Exception_stops_applying_once_expired_and_yields_a_new_evaluation()
    {
        PublishSpecExamples();
        var request = Request(paths: "src/Legacy/Homepage/Old.cs");
        var before = await _preflight.ExecuteAsync(_developer, request, default);

        _time.SetUtcNow(new DateTimeOffset(2027, 1, 1, 0, 0, 1, TimeSpan.Zero));
        var after = await _preflight.ExecuteAsync(_developer, request, default);

        Assert.NotEqual(before.Evaluation.Id, after.Evaluation.Id);
        Assert.Empty(after.Evaluation.AppliedExceptions);
        Assert.Equal(Importance.Required, Assert.Single(after.Evaluation.AppliedRecords).Importance);
    }

    [Fact]
    public async Task Identical_request_against_identical_state_returns_the_same_evaluation()
    {
        PublishSpecExamples();

        var first = await _preflight.ExecuteAsync(_developer, Request(paths: ["b.cs", "a.cs"]), default);
        _time.Advance(TimeSpan.FromMinutes(5));
        var second = await _preflight.ExecuteAsync(_developer, Request(paths: ["a.cs", "b.cs"]), default);

        Assert.Equal(first.Evaluation.Id, second.Evaluation.Id);
        Assert.Equal(first.Receipt.Digest, second.Receipt.Digest);
        Assert.Single(_store.Items);
    }

    [Fact]
    public async Task New_snapshot_or_catalog_version_invalidates_the_cached_evaluation()
    {
        PublishSpecExamples();
        var first = await _preflight.ExecuteAsync(_developer, Request(), default);

        _graph.Version = "cv_2";
        var afterCatalog = await _preflight.ExecuteAsync(_developer, Request(), default);
        _snapshots.Publish(Org, TestRecords.Decision("ARCH-100"));
        var afterSnapshot = await _preflight.ExecuteAsync(_developer, Request(), default);

        Assert.Equal(3, new[] { first.Evaluation.Id, afterCatalog.Evaluation.Id, afterSnapshot.Evaluation.Id }.Distinct().Count());
    }

    [Fact]
    public async Task Significant_change_requires_a_design_with_explained_triggers()
    {
        PublishSpecExamples();

        var (evaluation, _) = await _preflight.ExecuteAsync(_developer,
            Request("Add a new service that validates OAuth tokens", "src/Auth/TokenValidator.cs"), default);

        Assert.True(evaluation.Significance.IsSignificant);
        Assert.Contains(evaluation.Significance.Triggers, t => t.Code == "SECURITY_BOUNDARY");
        Assert.Contains(evaluation.Significance.Triggers, t => t.Code == "NEW_COMPONENT");
        Assert.All(evaluation.Significance.Triggers, t => Assert.NotEmpty(t.Evidence));
        Assert.Equal(Verdict.AllowWithWarnings, evaluation.Verdict);
        Assert.Contains(evaluation.Findings, f => f.Code == EvaluationPipeline.DesignRequired);
        Assert.Equal(PreflightService.ProduceDesign, evaluation.RequiredActions[0]);
    }

    [Fact]
    public async Task Unresolved_authority_conflict_is_never_allowed()
    {
        _snapshots.Publish(Org,
            TestRecords.Decision("ARCH-001", scope: Scope.Of((ScopeDimension.Repository, ["gateway"])), relations: [new Relation(RelationKind.ConflictsWith, "ARCH-002")]),
            TestRecords.Decision("ARCH-002", scope: Scope.Of((ScopeDimension.System, ["gui-platform"])), owners: ["gui-architects"]));

        var (evaluation, _) = await _preflight.ExecuteAsync(_developer, Request(), default);

        Assert.Equal(Verdict.RequireReview, evaluation.Verdict);
        var finding = Assert.Single(evaluation.Findings, f => f.Code == ConflictCodes.Unresolved);
        Assert.Equal(["ARCH-001", "ARCH-002"], finding.GovernanceIds.AsEnumerable());
        Assert.Equal(2, finding.Evidence.Length);
        Assert.Contains("gui-architects", evaluation.RequiredReviewers);
        Assert.Contains("team:platform", evaluation.RequiredReviewers);
    }

    [Fact]
    public async Task Unbound_repository_is_surfaced_and_keeps_scoped_records_applicable()
    {
        _snapshots.Publish(Org, TestRecords.Decision("ARCH-001", scope: Scope.Of((ScopeDimension.System, ["payments"]))));

        var (evaluation, _) = await _preflight.ExecuteAsync(_developer, Request() with { Repository = "unknown-repo" }, default);

        Assert.Contains(evaluation.Findings, f => f.Code == ChangeScopeResolver.RepositoryUnbound);
        Assert.NotEmpty(evaluation.ResolvedScope.Gaps);
        Assert.True(Assert.Single(evaluation.AppliedRecords).IsPotential);
        Assert.Equal(Verdict.AllowWithWarnings, evaluation.Verdict);
    }

    [Fact]
    public async Task Affected_dependencies_come_from_the_system_graph()
    {
        PublishSpecExamples();
        _graph.Edge("component:gui/portal", RelationType.DependsOn, "component:gui/api-gateway");
        _graph.Edge("component:mobile/app", RelationType.Consumes, "api:gateway/v1");

        var (evaluation, _) = await _preflight.ExecuteAsync(_developer, Request(), default);

        Assert.Equal(["component:gui/portal", "component:mobile/app"], evaluation.AffectedDependencies.Select(d => d.Entity));
    }

    [Fact]
    public async Task Missing_snapshot_is_an_error_and_never_an_allow()
    {
        var error = await Assert.ThrowsAsync<AxiomException>(() => _preflight.ExecuteAsync(_developer, Request(), default));

        Assert.Equal(ErrorCodes.NoPublishedSnapshot, error.Code);
        Assert.True(error.Retryable);
        Assert.Empty(_store.Items);
    }

    [Theory]
    [InlineData(Role.Reader)]
    [InlineData(Role.Auditor)]
    [InlineData(Role.ExceptionApprover)]
    public async Task Callers_without_the_evaluate_right_are_rejected(Role role)
    {
        PublishSpecExamples();
        var error = await Assert.ThrowsAsync<AxiomException>(() => _preflight.ExecuteAsync(Principals.With(Org, role), Request(), default));
        Assert.Equal(ErrorKind.Forbidden, error.Kind);
    }

    [Fact]
    public async Task Caller_cannot_evaluate_against_another_organization()
    {
        PublishSpecExamples();
        var error = await Assert.ThrowsAsync<AxiomException>(() => _preflight.ExecuteAsync(Principals.With("globex", Role.Contributor), Request(), default));
        Assert.Equal(ErrorKind.Forbidden, error.Kind);
    }

    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("src/../../x")]
    [InlineData("C:\\Windows\\x")]
    public async Task Traversal_and_absolute_paths_are_rejected(string path)
    {
        PublishSpecExamples();
        var error = await Assert.ThrowsAsync<AxiomException>(() => _preflight.ExecuteAsync(_developer, Request(paths: path), default));
        Assert.Equal(ErrorKind.Validation, error.Kind);
    }
}
