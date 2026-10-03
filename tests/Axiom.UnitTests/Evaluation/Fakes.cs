using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Application.Governance;
using Axiom.Domain.Audit;
using Axiom.Domain.Catalog;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;

namespace Axiom.UnitTests.Evaluation;

internal sealed class FakeSnapshots : IGovernanceSnapshotProvider
{
    private readonly Dictionary<string, GovernanceSnapshot> _snapshots = [];
    private string? _current;

    public DateTimeOffset PublishedAt { get; set; } = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    public GovernanceSnapshot Publish(string organization, params GovernanceRecord[] records)
    {
        var snapshot = GovernanceSnapshot.Create(organization, "commit-" + _snapshots.Count,
            records.Select(r => new RecordRevision(r, new SourceProvenance("governance", "abc", $"governance/{r.Id}.md", "blob"), 1)));
        _snapshots[snapshot.Id] = snapshot;
        _current = snapshot.Id;
        return snapshot;
    }

    public Task<SnapshotInfo?> GetCurrentInfoAsync(string organizationId, CancellationToken cancellationToken)
    {
        var current = _current is not null && _snapshots[_current].OrganizationId == organizationId ? _snapshots[_current] : null;
        return Task.FromResult(current is null ? null : new SnapshotInfo(current.Id, organizationId, current.SourceCommit, PublishedAt, current.Count));
    }

    public Task<GovernanceSnapshot?> GetCurrentAsync(string organizationId, CancellationToken cancellationToken) =>
        Task.FromResult(_current is not null && _snapshots[_current].OrganizationId == organizationId ? _snapshots[_current] : null);

    public Task<GovernanceSnapshot?> GetAsync(string organizationId, string snapshotId, CancellationToken cancellationToken) =>
        Task.FromResult(_snapshots.TryGetValue(snapshotId, out var s) && s.OrganizationId == organizationId ? s : null);
}

/// <summary>A small in-memory graph: enough topology to exercise the evaluation pipeline.</summary>
internal sealed class FakeGraph : ISystemGraph
{
    private readonly Dictionary<EntityRef, RepositoryTopology> _topologies = [];
    private readonly List<(EntityRef From, RelationType Relation, EntityRef To)> _edges = [];

    public string Version { get; set; } = "cv_1";

    public void Bind(RepositoryTopology topology) => _topologies[topology.Repository] = topology;

    public void Edge(string source, RelationType relation, string target) => _edges.Add((EntityRef.Parse(source), relation, EntityRef.Parse(target)));

    public Task<EntityRef?> ResolveRepositoryAsync(string organizationId, string repository, CancellationToken cancellationToken) =>
        Task.FromResult(_topologies.Keys.Where(k => k.IsDesignatedBy(repository)).Select(k => (EntityRef?)k).FirstOrDefault());

    public Task<SoftwareEntity?> FindAsync(string organizationId, EntityRef entity, CancellationToken cancellationToken) => Task.FromResult<SoftwareEntity?>(null);

    public Task<RepositoryTopology> GetRepositoryTopologyAsync(string organizationId, EntityRef repository, CancellationToken cancellationToken) =>
        Task.FromResult(_topologies[repository]);

    public Task<ImpactResult> TraverseAsync(string organizationId, ImpactQuery query, CancellationToken cancellationToken)
    {
        var impacted = _edges
            .Where(e => query.Relations.Contains(e.Relation) && e.To == query.Start)
            .Select(e => new ImpactedEntity(e.From, 1, [new PathStep(e.From, e.Relation, TraversalDirection.Upstream, e.To, true)]))
            .ToImmutableArray();
        return Task.FromResult(new ImpactResult(query.Start, impacted, ImmutableDictionary<EntityRef, ImmutableArray<EntityRef>>.Empty, false, Version));
    }

    public Task<GraphQualityReport> GetQualityReportAsync(string organizationId, TimeSpan staleAfter, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<GraphView> GetViewAsync(string organizationId, EntityRef? focus, int depth, int maxNodes, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<string> GetCatalogVersionAsync(string organizationId, CancellationToken cancellationToken) => Task.FromResult(Version);
}

internal sealed class InMemoryEvaluationStore : IEvaluationStore
{
    private readonly List<StoredEvaluation> _items = [];

    public List<IntegrationEvent> Events { get; } = [];

    public IReadOnlyList<StoredEvaluation> Items => _items;

    public Task<StoredEvaluation?> FindAsync(string organizationId, string evaluationId, CancellationToken cancellationToken) =>
        Task.FromResult(_items.FirstOrDefault(i => i.Evaluation.OrganizationId == organizationId && i.Evaluation.Id == evaluationId));

    public Task<StoredEvaluation> AppendAsync(EvaluationRecord evaluation, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken)
    {
        var existing = _items.FirstOrDefault(i => i.Evaluation.OrganizationId == evaluation.OrganizationId && i.Evaluation.Id == evaluation.Id);
        if (existing is not null)
        {
            return Task.FromResult(existing);
        }

        var previous = _items.LastOrDefault(i => i.Evaluation.OrganizationId == evaluation.OrganizationId)?.Receipt.ChainDigest ?? ReceiptFactory.GenesisDigest;
        var stored = new StoredEvaluation(evaluation, ReceiptFactory.Create(evaluation, previous));
        _items.Add(stored);
        Events.AddRange(events);
        return Task.FromResult(stored);
    }

    public Task<Receipt?> FindReceiptAsync(string organizationId, string receiptOrEvaluationId, CancellationToken cancellationToken) =>
        Task.FromResult(_items.Where(i => i.Evaluation.OrganizationId == organizationId && (i.Receipt.Id == receiptOrEvaluationId || i.Evaluation.Id == receiptOrEvaluationId)).Select(i => i.Receipt).FirstOrDefault());

    public Task<Receipt?> FindReceiptByCommitAsync(string organizationId, string repository, string commitSha, EvaluationStage? stage, CancellationToken cancellationToken) =>
        Task.FromResult(_items.Where(i => i.Evaluation.OrganizationId == organizationId && i.Receipt.Repository == repository && i.Receipt.CommitSha == commitSha && (stage is null || i.Receipt.Stage == stage))
            .Select(i => i.Receipt).LastOrDefault());

    public Task<(ImmutableArray<EvaluationSummary> Items, int Total)> SearchAsync(string organizationId, EvaluationQuery query, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public async IAsyncEnumerable<Receipt> ReadChainAsync(string organizationId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var item in _items.Where(i => i.Evaluation.OrganizationId == organizationId))
        {
            await Task.Yield();
            yield return item.Receipt;
        }
    }
}

internal static class Principals
{
    public static AxiomPrincipal With(string organization, params Role[] roles) =>
        new("user:alice", "Alice", organization, [.. roles], ImmutableHashSet<string>.Empty);
}
