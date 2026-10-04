using System.Collections.Immutable;
using Axiom.Application.Catalog;
using Axiom.Domain.Catalog;

namespace Axiom.UnitTests.Catalog;

public class ImpactAnalyzerTests
{
    private static EntityRef Ref(string value) => EntityRef.Parse(value);

    [Fact]
    public void An_api_change_walks_upstream_to_providers_consumers_repositories_and_owners()
    {
        var plan = ImpactPlanner.For(EntityKind.Api);

        Assert.Equal(TraversalDirection.Upstream, plan.Direct.Direction);
        Assert.Equal(
            [RelationType.Provides, RelationType.Consumes, RelationType.Implements, RelationType.Owns],
            plan.Direct.Relations.AsEnumerable());
        Assert.Empty(plan.Pivots);
    }

    [Fact]
    public void A_component_change_reaches_dependents_and_pivots_through_provided_apis()
    {
        var plan = ImpactPlanner.For(EntityKind.Component);

        Assert.Equal(TraversalDirection.Upstream, plan.Direct.Direction);
        Assert.Equal([RelationType.DependsOn, RelationType.Implements, RelationType.Owns], plan.Direct.Relations.AsEnumerable());
        var pivot = Assert.Single(plan.Pivots);
        Assert.Equal(TraversalDirection.Downstream, pivot.Direction);
        Assert.Equal([RelationType.Provides], pivot.Relations.AsEnumerable());
    }

    [Theory]
    [InlineData(EntityKind.Database)]
    [InlineData(EntityKind.Queue)]
    [InlineData(EntityKind.EventStream)]
    [InlineData(EntityKind.Resource)]
    [InlineData(EntityKind.DataProduct)]
    public void A_shared_resource_change_reaches_its_users_publishers_and_subscribers(EntityKind kind)
    {
        var plan = ImpactPlanner.For(kind);

        Assert.Equal(TraversalDirection.Upstream, plan.Direct.Direction);
        Assert.Contains(RelationType.StoresIn, plan.Direct.Relations);
        Assert.Contains(RelationType.PublishesTo, plan.Direct.Relations);
        Assert.Contains(RelationType.SubscribesTo, plan.Direct.Relations);
        Assert.Contains(RelationType.DependsOn, plan.Direct.Relations);
        Assert.Contains(RelationType.Owns, plan.Direct.Relations);
        Assert.DoesNotContain(RelationType.Contains, plan.Direct.Relations);
    }

    [Fact]
    public void Every_entity_kind_has_a_bounded_relation_specific_plan()
    {
        foreach (var kind in Enum.GetValues<EntityKind>())
        {
            var plan = ImpactPlanner.For(kind);
            Assert.NotEmpty(plan.Direct.Relations);
            Assert.True(plan.Direct.Relations.Length < Enum.GetValues<RelationType>().Length, $"{kind} must not walk every relation.");
        }

        Assert.Equal(TraversalDirection.Downstream, ImpactPlanner.For(EntityKind.System).Direct.Direction);
        Assert.Equal([RelationType.Implements], ImpactPlanner.For(EntityKind.Repository).Pivots.Single().Relations.AsEnumerable());
    }

    [Fact]
    public async Task A_repository_change_expands_through_its_components_and_their_apis()
    {
        var graph = new FakeGraph()
            .Edge("repo:gateway", RelationType.Implements, "component:gateway")
            .Edge("team:platform", RelationType.Owns, "component:gateway")
            .Edge("component:gateway", RelationType.Provides, "api:gateway/v1")
            .Edge("component:web", RelationType.Consumes, "api:gateway/v1")
            .Edge("repo:web", RelationType.Implements, "component:web")
            .Edge("team:web", RelationType.Owns, "component:web")
            .Edge("component:batch", RelationType.DependsOn, "component:gateway", isFact: false);
        graph.Known.Add(Ref("repo:gateway"));

        var analysis = await new ImpactAnalyzer(graph).AnalyzeAsync(
            new ImpactAnalysisRequest("acme", new ImpactChange("source", Ref("repo:gateway")), Depth: 4, IncludeUnconfirmed: true),
            CancellationToken.None);

        Assert.Equal(
            [
                "1 component:gateway", "2 component:batch", "2 api:gateway/v1", "2 team:platform",
                "3 component:web", "4 repo:web", "4 team:web",
            ],
            analysis.Affected.Select(a => $"{a.Depth} {a.Entity}"));
        Assert.DoesNotContain(analysis.Affected, a => a.Entity == Ref("repo:gateway"));

        var web = analysis.Affected.Single(a => a.Entity == Ref("component:web"));
        Assert.Equal(
            [
                "repo:gateway Implements Downstream component:gateway",
                "component:gateway Provides Downstream api:gateway/v1",
                "api:gateway/v1 Consumes Upstream component:web",
            ],
            web.Path.Select(s => $"{s.From} {s.Relation} {s.Direction} {s.To}"));
        Assert.Equal([Ref("team:web")], web.Owners.AsEnumerable());
        Assert.Equal(ImpactConfidence.Confirmed, web.Confidence);

        Assert.Equal(ImpactConfidence.Candidate, analysis.Affected.Single(a => a.Entity == Ref("component:batch")).Confidence);
        Assert.Contains(analysis.Gaps, g => g.Contains("candidates", StringComparison.Ordinal));
        Assert.Equal([Ref("team:platform"), Ref("team:web")], analysis.Owners.AsEnumerable());
        Assert.Equal("42", analysis.CatalogVersion);
        Assert.False(analysis.Truncated);
        Assert.Equal("source", analysis.Change.Kind);
    }

    [Fact]
    public async Task Depth_bounds_the_whole_analysis_including_pivots()
    {
        var graph = new FakeGraph()
            .Edge("component:gateway", RelationType.Provides, "api:gateway/v1")
            .Edge("component:web", RelationType.Consumes, "api:gateway/v1");
        graph.Known.Add(Ref("component:gateway"));

        var shallow = await new ImpactAnalyzer(graph).AnalyzeAsync(
            new ImpactAnalysisRequest("acme", new ImpactChange(null, Ref("component:gateway")), Depth: 1), CancellationToken.None);
        var clamped = await new ImpactAnalyzer(graph).AnalyzeAsync(
            new ImpactAnalysisRequest("acme", new ImpactChange(null, Ref("component:gateway")), Depth: 99), CancellationToken.None);

        Assert.Equal(["api:gateway/v1"], shallow.Affected.Select(a => a.Entity.ToString()));
        Assert.Equal(["api:gateway/v1", "component:web"], clamped.Affected.Select(a => a.Entity.ToString()));
        Assert.All(graph.Queries, q => Assert.InRange(q.MaxDepth, 1, ISystemGraph.MaxTraversalDepth));
    }

    [Fact]
    public async Task An_unknown_entity_is_reported_as_a_gap_and_unconfirmed_edges_are_not_requested_by_default()
    {
        var graph = new FakeGraph().Edge("component:web", RelationType.Consumes, "api:ghost", isFact: false);

        var analysis = await new ImpactAnalyzer(graph).AnalyzeAsync(
            new ImpactAnalysisRequest("acme", new ImpactChange("api_or_contract", Ref("api:ghost")), Depth: 2), CancellationToken.None);

        Assert.Empty(analysis.Affected);
        Assert.Contains(analysis.Gaps, g => g.StartsWith("api:ghost is not described", StringComparison.Ordinal));
        Assert.All(graph.Queries, q => Assert.False(q.IncludeUnconfirmed));
    }

    [Fact]
    public async Task Too_many_pivot_entities_mark_the_result_truncated()
    {
        var graph = new FakeGraph();
        for (var i = 0; i < ImpactAnalyzer.MaxPivotExpansions + 5; i++)
        {
            graph.Edge("component:hub", RelationType.Provides, $"api:hub/v{i:00}");
        }

        var analysis = await new ImpactAnalyzer(graph).AnalyzeAsync(
            new ImpactAnalysisRequest("acme", new ImpactChange(null, Ref("component:hub")), Depth: 3), CancellationToken.None);

        Assert.True(analysis.Truncated);
        Assert.Equal(ImpactAnalyzer.MaxPivotExpansions + 5, analysis.Affected.Length);
        // One direct traversal, one pivot hop, then one traversal per expanded API and none beyond the bound.
        Assert.Equal(2 + ImpactAnalyzer.MaxPivotExpansions, graph.Queries.Count);
    }

    /// <summary>In-memory graph honouring the traversal contract: relation-specific, direction-aware, shortest path, depth-bounded.</summary>
    private sealed class FakeGraph : ISystemGraph
    {
        private readonly List<(EntityRef From, RelationType Relation, EntityRef To, bool IsFact)> _edges = [];

        public HashSet<EntityRef> Known { get; } = [];

        public List<ImpactQuery> Queries { get; } = [];

        public FakeGraph Edge(string from, RelationType relation, string target, bool isFact = true)
        {
            _edges.Add((Ref(from), relation, Ref(target), isFact));
            return this;
        }

        public Task<ImpactResult> TraverseAsync(string organizationId, ImpactQuery query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            var paths = new Dictionary<EntityRef, ImmutableArray<PathStep>> { [query.Start] = [] };
            var frontier = new List<EntityRef> { query.Start };
            for (var depth = 0; depth < query.MaxDepth && frontier.Count > 0; depth++)
            {
                var next = new List<EntityRef>();
                foreach (var current in frontier)
                {
                    foreach (var edge in _edges.Where(e => query.Relations.Contains(e.Relation) && (e.IsFact || query.IncludeUnconfirmed)))
                    {
                        var (near, far) = query.Direction == TraversalDirection.Downstream ? (edge.From, edge.To) : (edge.To, edge.From);
                        if (near == current && !paths.ContainsKey(far))
                        {
                            paths[far] = paths[current].Add(new PathStep(current, edge.Relation, query.Direction, far, edge.IsFact));
                            next.Add(far);
                        }
                    }
                }

                frontier = next;
            }

            var owners = paths.Keys.ToImmutableDictionary(
                e => e,
                e => _edges.Where(x => x.Relation == RelationType.Owns && x.To == e).Select(x => x.From).ToImmutableArray());
            return Task.FromResult(new ImpactResult(
                query.Start,
                [.. paths.Where(p => p.Key != query.Start).Select(p => new ImpactedEntity(p.Key, p.Value.Length, p.Value)).OrderBy(i => i.Depth).ThenBy(i => i.Entity)],
                owners.Where(o => !o.Value.IsEmpty).ToImmutableDictionary(),
                false,
                "42"));
        }

        public Task<SoftwareEntity?> FindAsync(string organizationId, EntityRef entity, CancellationToken cancellationToken) =>
            Task.FromResult(Known.Contains(entity)
                ? new SoftwareEntity(entity, entity.SimpleName, null, [], ImmutableSortedDictionary<string, string>.Empty,
                    EntityProvenance.Declared(ProvenanceSource.Catalog, "test", DateTimeOffset.UnixEpoch))
                : null);

        public Task<string> GetCatalogVersionAsync(string organizationId, CancellationToken cancellationToken) => Task.FromResult("42");

        public Task<EntityRef?> ResolveRepositoryAsync(string organizationId, string repository, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RepositoryTopology> GetRepositoryTopologyAsync(string organizationId, EntityRef repository, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<GraphQualityReport> GetQualityReportAsync(string organizationId, TimeSpan staleAfter, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<GraphView> GetViewAsync(string organizationId, EntityRef? focus, int depth, int maxNodes, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
