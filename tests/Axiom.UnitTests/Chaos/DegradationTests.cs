using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Application.Governance;
using Axiom.Application.Policy;
using Axiom.Application.Review;
using Axiom.Application.Semantic;
using Axiom.Domain.Audit;
using Axiom.Domain.Catalog;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Axiom.Domain.Policy;
using Axiom.Domain.Review;
using Axiom.Infrastructure.Semantic;
using Axiom.UnitTests.Evaluation;
using Axiom.UnitTests.Governance;
using Axiom.UnitTests.Policy;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Axiom.UnitTests.Chaos;

/// <summary>
/// Task 12.5: dependency failures with fakes. R24 says the deterministic result continues and semantic
/// coverage is reported as degraded; every other failure is an error that carries no verdict, so it can
/// never be mistaken for ALLOW (MCP contract: "a tool error is never ALLOW").
/// </summary>
public class DegradationTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);
    private const string Org = "acme";
    private const string BlockedPath = "src/Generated/Client.cs";
    private const string RoutedPath = "src/Routing/RoleRouter.cs";
    private const string Forbidden = "Add domain database access to the Gateway for routing decisions.";

    private readonly FlakySnapshots _snapshots = new();
    private readonly FlakyGraph _graph = new();
    private readonly FlakyStore _store = new();
    private readonly FlakyScm _scm = new();
    private readonly ChaosProvider _provider = new();
    private readonly SemanticOptions _options = new() { Enabled = true, TimeoutSeconds = 1 };
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly AxiomPrincipal _developer = Principals.With(Org, Role.Contributor);

    public DegradationTests()
    {
        _graph.Bind(new RepositoryTopology(EntityRef.Parse("repo:gateway"), [EntityRef.Parse("component:gui/api-gateway")], [EntityRef.Parse("system:gui-platform")],
            [], [], [], [], [EntityRef.Parse("team:platform")], [], []));
        _snapshots.Publish(Org,
            TestRecords.Decision("ARCH-001", scope: Scope.Of((ScopeDimension.Repository, ["gateway"])),
                rules: [PolicyTestKit.Bind("forbidden-path-change", EnforcementLevel.Block, ("paths", "src/Generated/**"))]) with
            {
                Forbidden = [Forbidden],
            });
        Changes(PolicyTestKit.Added(RoutedPath, "var role = await db.Users.Where(u => u.Id == id).SingleAsync();"));
    }

    private void Changes(params ChangedFile[] files) => _scm.Diff = new ScmDiff([.. files], InMemoryRepositoryView.FromChanges(files), false, files.Length);

    private Task<StoredEvaluation> RunAsync(bool pullRequest = false, bool semantic = true, CancellationToken cancellationToken = default)
    {
        var analyzer = semantic ? new SemanticAnalyzer(Options.Create(_options), NullLogger<SemanticAnalyzer>.Instance, _provider) : null;
        var service = new DiffEvaluationService(
            new EvaluationPipeline(_snapshots, new ChangeScopeResolver(_graph), _graph, _store, _time),
            _scm,
            new PolicyEngine(PolicyRuleCatalog.BuiltIn),
            PolicyRuleCatalog.BuiltIn,
            _store,
            new NoReviews(),
            analyzer);
        var request = new DiffValidationRequest(Org, "gateway", "aaaaaaa1", "bbbbbbb2", "feature/x", pullRequest ? "42" : null, null, "kiro", "s1");
        return pullRequest ? service.ExecutePullRequestAsync(_developer, request, cancellationToken) : service.ExecuteAsync(_developer, request, cancellationToken);
    }

    private static RawSemanticFinding Raw(double confidence, string record = "ARCH-001", string path = RoutedPath) =>
        new(SemanticFindingKind.DecisionConflict, record, path, 1, "The router reads domain state directly.", confidence);

    private static void AssertDegradedButDeterministic(StoredEvaluation stored, Verdict expected)
    {
        Assert.Equal(expected, stored.Evaluation.Verdict);
        Assert.Equal(SemanticCoverageStatus.Degraded, stored.Evaluation.SemanticCoverage.Status);
        Assert.DoesNotContain(stored.Evaluation.Findings, f => f.Source == FindingSource.Semantic);
        Assert.True(ReceiptFactory.Verify(stored.Receipt));
        using var payload = JsonDocument.Parse(stored.Receipt.Payload);
        Assert.Equal("Degraded", payload.RootElement.GetProperty("semantic").GetProperty("status").GetString());
    }

    // ---- LLM provider down / slow / garbage (R24) ----

    public static TheoryData<Exception> ProviderFailures =>
    [
        new HttpRequestException("503 Service Unavailable, key sk-abcdefghijklmnopqrstuvwxyz"),
        new TimeoutException("connect timed out"),
        new SemanticOutputException("the model answered in prose"),
        new JsonException("unexpected end of input"),
        new InvalidOperationException("provider bug"),
    ];

    [Theory]
    [MemberData(nameof(ProviderFailures))]
    public async Task A_provider_that_fails_leaves_the_deterministic_block_intact_and_degrades_coverage(Exception failure)
    {
        _provider.Failure = failure;
        Changes(PolicyTestKit.Added(BlockedPath, "class Client;"));

        var stored = await RunAsync(pullRequest: true);

        AssertDegradedButDeterministic(stored, Verdict.Block);
        Assert.DoesNotContain("sk-abc", stored.Evaluation.SemanticCoverage.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abc", stored.Receipt.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_outage_on_a_clean_change_is_visibly_degraded_not_silently_complete()
    {
        _provider.Failure = new HttpRequestException("down");

        var stored = await RunAsync();

        AssertDegradedButDeterministic(stored, Verdict.Allow);
        Assert.False(string.IsNullOrWhiteSpace(stored.Evaluation.SemanticCoverage.Reason));
    }

    [Fact]
    public async Task A_slow_provider_is_cut_off_at_the_timeout_and_the_block_stands()
    {
        _provider.Hang = true;
        Changes(PolicyTestKit.Added(BlockedPath, "class Client;"));

        var stored = await RunAsync();

        AssertDegradedButDeterministic(stored, Verdict.Block);
        Assert.Contains("timed out", stored.Evaluation.SemanticCoverage.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_provider_that_ignores_cancellation_cannot_hold_the_pipeline_hostage_beyond_its_own_failure()
    {
        // A provider that fails after a delay (instead of honouring the token) still ends in degraded coverage.
        _provider.Failure = new HttpRequestException("reset by peer");
        _provider.Delay = TimeSpan.FromMilliseconds(100);

        var stored = await RunAsync();

        AssertDegradedButDeterministic(stored, Verdict.Allow);
    }

    public static TheoryData<double> ImplausibleConfidences =>
    [
        double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1.5, -0.2, 100,
    ];

    [Theory]
    [MemberData(nameof(ImplausibleConfidences))]
    public async Task Garbage_that_parses_but_is_out_of_contract_is_dropped_and_the_evaluation_is_still_recordable(double confidence)
    {
        _provider.Findings = [Raw(confidence)];

        var stored = await RunAsync();

        Assert.Equal(Verdict.Allow, stored.Evaluation.Verdict);
        Assert.DoesNotContain(stored.Evaluation.Findings, f => f.Source == FindingSource.Semantic);
        Assert.True(ReceiptFactory.Verify(stored.Receipt));
        _ = JsonSerializer.Serialize(stored.Evaluation, WebOptions);
    }

    [Fact]
    public async Task Findings_that_cite_records_or_files_outside_the_packet_are_dropped()
    {
        _provider.Findings = [Raw(0.99, record: "ARCH-999"), Raw(0.99, path: "src/NotInTheDiff.cs"), Raw(0.99, record: "../../etc/passwd")];

        var stored = await RunAsync();

        Assert.Equal(Verdict.Allow, stored.Evaluation.Verdict);
        Assert.DoesNotContain(stored.Evaluation.Findings, f => f.Source == FindingSource.Semantic);
        Assert.Equal(SemanticCoverageStatus.Complete, stored.Evaluation.SemanticCoverage.Status);
    }

    [Fact]
    public async Task A_flood_of_findings_is_capped_and_never_blocks()
    {
        _provider.Findings = [.. Enumerable.Range(1, 500).Select(_ => Raw(0.99))];

        var stored = await RunAsync(pullRequest: true);

        Assert.True(stored.Evaluation.Findings.Count(f => f.Source == FindingSource.Semantic) <= _options.MaxFindings);
        Assert.True(stored.Evaluation.Verdict < Verdict.Block);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_mistaken_for_a_provider_outage()
    {
        _provider.Hang = true;
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(cancellationToken: cancel.Token));

        Assert.Empty(_store.Items);
    }

    [Fact]
    public async Task With_the_provider_down_the_same_request_is_recorded_once_and_replays_identically()
    {
        _provider.Failure = new HttpRequestException("down");

        var first = await RunAsync();
        var second = await RunAsync();

        Assert.Equal(first.Receipt, second.Receipt);
        Assert.Single(_store.Items);
    }

    // ---- Vector / search / projection store unavailable ----

    [Fact]
    public async Task The_evaluation_path_has_no_search_or_vector_dependency_so_the_deterministic_result_does_not_need_one()
    {
        // Evaluation reads only the published snapshot, the System Graph and the SCM. Governance search (full text
        // and vector) is a read-side feature; with the semantic stage off, the evaluation completes without it.
        Changes(PolicyTestKit.Added(BlockedPath, "class Client;"));

        var stored = await RunAsync(semantic: false);

        Assert.Equal(Verdict.Block, stored.Evaluation.Verdict);
        Assert.Equal(SemanticCoverageStatus.NotRequested, stored.Evaluation.SemanticCoverage.Status);
    }

    [Fact]
    public async Task An_unavailable_governance_projection_is_an_error_not_a_verdict()
    {
        _snapshots.Failure = new InvalidOperationException("connection refused");

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync());

        Assert.IsNotType<StoredEvaluation>(thrown);
        Assert.Empty(_store.Items);
    }

    [Fact]
    public async Task An_organization_without_a_published_snapshot_cannot_be_evaluated()
    {
        var other = Principals.With("globex", Role.Contributor);
        var service = new DiffEvaluationService(
            new EvaluationPipeline(_snapshots, new ChangeScopeResolver(_graph), _graph, _store, _time),
            _scm, new PolicyEngine(PolicyRuleCatalog.BuiltIn), PolicyRuleCatalog.BuiltIn, _store, new NoReviews());

        var error = await Assert.ThrowsAsync<AxiomException>(() =>
            service.ExecuteAsync(other, new DiffValidationRequest("globex", "gateway", "aaaaaaa1", "bbbbbbb2", null, null, null, null, null), default));

        Assert.Equal((ErrorKind.Unavailable, ErrorCodes.NoPublishedSnapshot, true), (error.Kind, error.Code, error.Retryable));
        Assert.Empty(_store.Items);
    }

    [Fact]
    public async Task A_failing_system_graph_stops_the_evaluation_instead_of_guessing_the_scope()
    {
        _graph.Failure = new TimeoutException("catalog database timed out");

        await Assert.ThrowsAsync<TimeoutException>(() => RunAsync());

        Assert.Empty(_store.Items);
    }

    // ---- SCM / Git source failing ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_scm_failure_yields_an_unavailable_error_and_no_evaluation(bool pullRequest)
    {
        _scm.Failure = new AxiomException(ErrorKind.Unavailable, ErrorCodes.ScmUnavailable, "The diff could not be read.", retryable: true);

        var error = await Assert.ThrowsAsync<AxiomException>(() => RunAsync(pullRequest));

        Assert.Equal((ErrorKind.Unavailable, ErrorCodes.ScmUnavailable, true), (error.Kind, error.Code, error.Retryable));
        Assert.Empty(_store.Items);
    }

    [Fact]
    public async Task An_unexpected_scm_exception_also_produces_no_verdict()
    {
        _scm.Failure = new HttpRequestException("git host unreachable");

        await Assert.ThrowsAsync<HttpRequestException>(() => RunAsync(pullRequest: true));

        Assert.Empty(_store.Items);
        Assert.Empty(_store.Events);
    }

    [Fact]
    public async Task A_truncated_diff_is_never_reported_as_a_clean_allow()
    {
        var files = new[] { PolicyTestKit.Added(RoutedPath, "class A;") };
        _scm.Diff = new ScmDiff([.. files], InMemoryRepositoryView.FromChanges(files), Truncated: true, TotalFiles: 5000);

        var stored = await RunAsync();

        Assert.Contains(stored.Evaluation.Findings, f => f.Code == DiffFindingCodes.DiffTruncated);
        Assert.True(stored.Evaluation.Verdict >= Verdict.RequireReview);
    }

    // ---- Database failing at receipt append ----

    [Fact]
    public async Task A_database_failure_at_receipt_append_returns_unavailable_and_counts_a_receipt_failure()
    {
        _store.AppendFailure = new InvalidOperationException("could not serialize access due to concurrent update");
        var before = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (ReferenceEquals(instrument, AxiomTelemetry.ReceiptFailures))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref before, value));
        listener.Start();
        Changes(PolicyTestKit.Added(BlockedPath, "class Client;"));

        var error = await Assert.ThrowsAsync<AxiomException>(() => RunAsync(pullRequest: true));

        Assert.Equal((ErrorKind.Unavailable, ErrorCodes.GovernanceUnavailable, true), (error.Kind, error.Code, error.Retryable));
        Assert.Same(_store.AppendFailure, error.InnerException);
        Assert.Contains("no verdict", error.Message, StringComparison.Ordinal);
        Assert.True(Interlocked.Read(ref before) >= 1, "axiom.receipt.failures was not incremented");
        Assert.Empty(_store.Items);
        Assert.Empty(_store.Events);
    }

    [Fact]
    public async Task After_the_database_recovers_the_retry_succeeds_with_the_same_evaluation_id_and_one_receipt()
    {
        Changes(PolicyTestKit.Added(BlockedPath, "class Client;"));
        _store.AppendFailure = new TimeoutException("db timeout");
        await Assert.ThrowsAsync<AxiomException>(() => RunAsync(pullRequest: true));
        _store.AppendFailure = null;

        var stored = await RunAsync(pullRequest: true);
        var again = await RunAsync(pullRequest: true);

        Assert.Equal(Verdict.Block, stored.Evaluation.Verdict);
        Assert.Equal(ReceiptFactory.GenesisDigest, stored.Receipt.PreviousChainDigest);
        Assert.Equal(stored.Receipt, again.Receipt);
        Assert.Single(_store.Items);
        Assert.Equal(1, _store.Events.Count(e => e.EventType == EventTypes.EvaluationBlocked));
    }

    [Fact]
    public async Task Cancellation_during_append_is_not_reported_as_a_receipt_failure()
    {
        _store.AppendFailure = new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync());

        Assert.Empty(_store.Items);
    }

    [Fact]
    public async Task A_failure_while_reading_the_existing_evaluation_also_yields_no_verdict()
    {
        _store.FindFailure = new InvalidOperationException("connection reset");

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync());

        Assert.Empty(_store.Items);
    }

    // ---- fakes ----

    private sealed class ChaosProvider : ISemanticProvider
    {
        public Exception? Failure { get; set; }

        public bool Hang { get; set; }

        public TimeSpan Delay { get; set; }

        public ImmutableArray<RawSemanticFinding> Findings { get; set; } = [];

        public string Name => "chaos";

        public string Model => "model-x";

        public bool IsExternal => false;

        public async Task<SemanticProviderOutput> AnalyzeAsync(EvidencePacket packet, CancellationToken cancellationToken)
        {
            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, CancellationToken.None);
            }

            return Failure is null ? new SemanticProviderOutput(Findings, new SemanticUsage(1, 1)) : throw Failure;
        }
    }

    private sealed class FlakySnapshots : IGovernanceSnapshotProvider
    {
        private readonly FakeSnapshots _inner = new();

        public Exception? Failure { get; set; }

        public GovernanceSnapshot Publish(string organization, params GovernanceRecord[] records) => _inner.Publish(organization, records);

        public Task<SnapshotInfo?> GetCurrentInfoAsync(string organizationId, CancellationToken cancellationToken) =>
            Failure is null ? _inner.GetCurrentInfoAsync(organizationId, cancellationToken) : Task.FromException<SnapshotInfo?>(Failure);

        public Task<GovernanceSnapshot?> GetCurrentAsync(string organizationId, CancellationToken cancellationToken) =>
            Failure is null ? _inner.GetCurrentAsync(organizationId, cancellationToken) : Task.FromException<GovernanceSnapshot?>(Failure);

        public Task<GovernanceSnapshot?> GetAsync(string organizationId, string snapshotId, CancellationToken cancellationToken) =>
            Failure is null ? _inner.GetAsync(organizationId, snapshotId, cancellationToken) : Task.FromException<GovernanceSnapshot?>(Failure);
    }

    private sealed class FlakyGraph : ISystemGraph
    {
        private readonly FakeGraph _inner = new();

        public Exception? Failure { get; set; }

        public void Bind(RepositoryTopology topology) => _inner.Bind(topology);

        private Task<T> Guard<T>(Func<Task<T>> call) => Failure is null ? call() : Task.FromException<T>(Failure);

        public Task<EntityRef?> ResolveRepositoryAsync(string organizationId, string repository, CancellationToken cancellationToken) =>
            Guard(() => _inner.ResolveRepositoryAsync(organizationId, repository, cancellationToken));

        public Task<SoftwareEntity?> FindAsync(string organizationId, EntityRef entity, CancellationToken cancellationToken) =>
            Guard(() => _inner.FindAsync(organizationId, entity, cancellationToken));

        public Task<RepositoryTopology> GetRepositoryTopologyAsync(string organizationId, EntityRef repository, CancellationToken cancellationToken) =>
            Guard(() => _inner.GetRepositoryTopologyAsync(organizationId, repository, cancellationToken));

        public Task<ImpactResult> TraverseAsync(string organizationId, ImpactQuery query, CancellationToken cancellationToken) =>
            Guard(() => _inner.TraverseAsync(organizationId, query, cancellationToken));

        public Task<GraphQualityReport> GetQualityReportAsync(string organizationId, TimeSpan staleAfter, CancellationToken cancellationToken) =>
            Guard(() => _inner.GetQualityReportAsync(organizationId, staleAfter, cancellationToken));

        public Task<GraphView> GetViewAsync(string organizationId, EntityRef? focus, int depth, int maxNodes, CancellationToken cancellationToken) =>
            Guard(() => _inner.GetViewAsync(organizationId, focus, depth, maxNodes, cancellationToken));

        public Task<string> GetCatalogVersionAsync(string organizationId, CancellationToken cancellationToken) =>
            Guard(() => _inner.GetCatalogVersionAsync(organizationId, cancellationToken));
    }

    private sealed class FlakyStore : IEvaluationStore
    {
        private readonly InMemoryEvaluationStore _inner = new();

        public Exception? AppendFailure { get; set; }

        public Exception? FindFailure { get; set; }

        public IReadOnlyList<StoredEvaluation> Items => _inner.Items;

        public List<IntegrationEvent> Events => _inner.Events;

        public Task<StoredEvaluation?> FindAsync(string organizationId, string evaluationId, CancellationToken cancellationToken) =>
            FindFailure is null ? _inner.FindAsync(organizationId, evaluationId, cancellationToken) : Task.FromException<StoredEvaluation?>(FindFailure);

        public Task<StoredEvaluation> AppendAsync(EvaluationRecord evaluation, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken) =>
            AppendFailure is null ? _inner.AppendAsync(evaluation, events, cancellationToken) : Task.FromException<StoredEvaluation>(AppendFailure);

        public Task<Receipt?> FindReceiptAsync(string organizationId, string receiptOrEvaluationId, CancellationToken cancellationToken) =>
            _inner.FindReceiptAsync(organizationId, receiptOrEvaluationId, cancellationToken);

        public Task<Receipt?> FindReceiptByCommitAsync(string organizationId, string repository, string commitSha, EvaluationStage? stage, CancellationToken cancellationToken) =>
            _inner.FindReceiptByCommitAsync(organizationId, repository, commitSha, stage, cancellationToken);

        public Task<(ImmutableArray<EvaluationSummary> Items, int Total)> SearchAsync(string organizationId, EvaluationQuery query, CancellationToken cancellationToken) =>
            _inner.SearchAsync(organizationId, query, cancellationToken);

        public IAsyncEnumerable<Receipt> ReadChainAsync(string organizationId, CancellationToken cancellationToken) =>
            _inner.ReadChainAsync(organizationId, cancellationToken);
    }

    private sealed class FlakyScm : IScmDiffSource
    {
        public ScmDiff Diff { get; set; } = new([], InMemoryRepositoryView.Empty, false, 0);

        public Exception? Failure { get; set; }

        public Task<ScmDiff> GetDiffAsync(ScmDiffRequest request, CancellationToken cancellationToken) =>
            Failure is null ? Task.FromResult(Diff) : Task.FromException<ScmDiff>(Failure);
    }

    private sealed class NoReviews : IReviewStore
    {
        public Task AppendAsync(ReviewDecision decision, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<ImmutableArray<ReviewDecision>> ListAsync(string organizationId, string evaluationId, CancellationToken cancellationToken) =>
            Task.FromResult(ImmutableArray<ReviewDecision>.Empty);

        public Task<ImmutableArray<ReviewQueueItem>> ListPendingAsync(string organizationId, int skip, int take, CancellationToken cancellationToken) =>
            Task.FromResult(ImmutableArray<ReviewQueueItem>.Empty);
    }
}
