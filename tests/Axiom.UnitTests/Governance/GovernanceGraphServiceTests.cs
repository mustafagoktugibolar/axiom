using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Application.Query;
using Axiom.Domain.Governance;
using Axiom.UnitTests.Evaluation;
using Microsoft.Extensions.Time.Testing;

namespace Axiom.UnitTests.Governance;

public class GovernanceGraphServiceTests
{
    private const string Org = "acme";
    private readonly FakeSnapshots _snapshots = new();
    private readonly GovernanceGraphService _service;
    private readonly AxiomPrincipal _reader = new("user:alice", "Alice", Org, [Role.Reader], []);

    public GovernanceGraphServiceTests() =>
        _service = new GovernanceGraphService(_snapshots, new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero)));

    private static Relation Rel(RelationKind kind, string target) => new(kind, target);

    private Task<GovernanceGraphDto> Get(GovernanceGraphRequest? request = null, AxiomPrincipal? principal = null) =>
        _service.GetAsync(principal ?? _reader, request ?? new GovernanceGraphRequest(), CancellationToken.None);

    [Fact]
    public async Task Requires_governance_read_access()
    {
        _snapshots.Publish(Org, TestRecords.Decision("A"));
        var nobody = new AxiomPrincipal("user:eve", null, Org, [], []);
        var error = await Assert.ThrowsAsync<AxiomException>(() => Get(principal: nobody));
        Assert.Equal(ErrorKind.Forbidden, error.Kind);
    }

    [Fact]
    public async Task Returns_nodes_with_status_and_authority_and_edges_between_them()
    {
        _snapshots.Publish(Org,
            TestRecords.Decision("A", relations: [Rel(RelationKind.Refines, "B"), Rel(RelationKind.Related, "MISSING")]),
            TestRecords.Decision("B", level: AuthorityLevel.Organization),
            TestRecords.Decision("C", LifecycleStatus.Proposed));

        var graph = await Get();

        Assert.Equal(["A", "B", "C"], graph.Nodes.Select(n => n.Id));
        var b = graph.Nodes.Single(n => n.Id == "B");
        Assert.Equal("Organization", b.AuthorityLevel);
        Assert.True(b.Authoritative);
        var c = graph.Nodes.Single(n => n.Id == "C");
        Assert.Equal("Proposed", c.Status);
        Assert.False(c.Authoritative);
        Assert.Equal(new GovernanceGraphEdge("A", "Refines", "B"), Assert.Single(graph.Edges));
        Assert.False(graph.Truncated);
    }

    [Fact]
    public async Task Empty_organization_gives_empty_graph_and_unknown_focus_is_not_found()
    {
        var graph = await Get();
        Assert.Empty(graph.Nodes);
        var error = await Assert.ThrowsAsync<AxiomException>(() => Get(new GovernanceGraphRequest { Focus = "X" }));
        Assert.Equal(ErrorKind.NotFound, error.Kind);
    }

    [Fact]
    public async Task Node_bound_truncates_deterministically_and_drops_edges_to_omitted_nodes()
    {
        _snapshots.Publish(Org,
            TestRecords.Decision("A", relations: [Rel(RelationKind.Related, "B"), Rel(RelationKind.Related, "C")]),
            TestRecords.Decision("B"),
            TestRecords.Decision("C"));

        var graph = await Get(new GovernanceGraphRequest { MaxNodes = 2 });

        Assert.True(graph.Truncated);
        Assert.Equal(["A", "B"], graph.Nodes.Select(n => n.Id));
        Assert.Equal("B", Assert.Single(graph.Edges).To);
    }

    [Fact]
    public async Task Edge_bound_truncates()
    {
        _snapshots.Publish(Org,
            TestRecords.Decision("A", relations: [Rel(RelationKind.Related, "B"), Rel(RelationKind.Refines, "C")]),
            TestRecords.Decision("B"),
            TestRecords.Decision("C"));

        var graph = await Get(new GovernanceGraphRequest { MaxEdges = 1 });

        Assert.True(graph.Truncated);
        Assert.Single(graph.Edges);
    }

    [Fact]
    public async Task Bounds_are_clamped()
    {
        _snapshots.Publish(Org, TestRecords.Decision("A"), TestRecords.Decision("B"));
        var graph = await Get(new GovernanceGraphRequest { MaxNodes = -5, Depth = 999 });
        Assert.Single(graph.Nodes);
        Assert.Equal(GovernanceGraphService.MaxDepth, graph.Depth);
    }

    [Fact]
    public async Task Focus_follows_relations_in_both_directions_up_to_depth()
    {
        // D -> C -> B -> A (each refines the next); E is unrelated.
        _snapshots.Publish(Org,
            TestRecords.Decision("A"),
            TestRecords.Decision("B", relations: [Rel(RelationKind.Refines, "A")]),
            TestRecords.Decision("C", relations: [Rel(RelationKind.Refines, "B")]),
            TestRecords.Decision("D", relations: [Rel(RelationKind.Refines, "C")]),
            TestRecords.Decision("E"));

        var one = await Get(new GovernanceGraphRequest { Focus = "B", Depth = 1 });
        Assert.Equal(["A", "B", "C"], one.Nodes.Select(n => n.Id));
        Assert.True(one.Nodes.Single(n => n.Id == "B").Focus);
        Assert.Equal(2, one.Edges.Length);

        var two = await Get(new GovernanceGraphRequest { Focus = "B", Depth = 2 });
        Assert.Equal(["A", "B", "C", "D"], two.Nodes.Select(n => n.Id));
        Assert.DoesNotContain(two.Nodes, n => n.Id == "E");
    }

    [Fact]
    public async Task Focus_with_node_bound_is_truncated_and_keeps_the_focus()
    {
        _snapshots.Publish(Org,
            TestRecords.Decision("HUB", relations: [Rel(RelationKind.Related, "A"), Rel(RelationKind.Related, "B"), Rel(RelationKind.Related, "C")]),
            TestRecords.Decision("A"),
            TestRecords.Decision("B"),
            TestRecords.Decision("C"));

        var graph = await Get(new GovernanceGraphRequest { Focus = "HUB", MaxNodes = 2 });

        Assert.True(graph.Truncated);
        Assert.Equal(["A", "HUB"], graph.Nodes.Select(n => n.Id));
    }

    [Fact]
    public async Task Filters_by_kind_status_and_scope()
    {
        _snapshots.Publish(Org,
            TestRecords.Decision("DEC-1", scope: Scope.Of((ScopeDimension.Repository, ["gateway"]))),
            TestRecords.Decision("DEC-2", LifecycleStatus.Superseded, scope: Scope.Of((ScopeDimension.Repository, ["billing"]))),
            TestRecords.Decision("STD-1", kind: RecordKind.Standard));

        var byKind = await Get(new GovernanceGraphRequest { Kinds = [RecordKind.Standard] });
        Assert.Equal(["STD-1"], byKind.Nodes.Select(n => n.Id));

        var byStatus = await Get(new GovernanceGraphRequest { Statuses = [LifecycleStatus.Superseded] });
        Assert.Equal(["DEC-2"], byStatus.Nodes.Select(n => n.Id));

        var byScope = await Get(new GovernanceGraphRequest { Scope = ImmutableDictionary<ScopeDimension, string>.Empty.Add(ScopeDimension.Repository, "GATEWAY") });
        Assert.Equal(["DEC-1"], byScope.Nodes.Select(n => n.Id));
    }

    [Fact]
    public async Task Filters_apply_to_neighbours_during_focus_traversal()
    {
        _snapshots.Publish(Org,
            TestRecords.Decision("A", relations: [Rel(RelationKind.Related, "B"), Rel(RelationKind.Related, "C")]),
            TestRecords.Decision("B", LifecycleStatus.Proposed),
            TestRecords.Decision("C"));

        var graph = await Get(new GovernanceGraphRequest { Focus = "A", Statuses = [LifecycleStatus.Accepted] });

        Assert.Equal(["A", "C"], graph.Nodes.Select(n => n.Id));
    }

    [Fact]
    public async Task Superseded_chain_is_reported_as_supersedes_edges_in_one_direction()
    {
        // NEW supersedes MID; MID is marked supersededBy NEW too (mirror, deduplicated); MID supersededBy... OLD declares it is superseded by MID.
        _snapshots.Publish(Org,
            TestRecords.Decision("NEW", relations: [Rel(RelationKind.Supersedes, "MID")]),
            TestRecords.Decision("MID", LifecycleStatus.Superseded, relations: [Rel(RelationKind.SupersededBy, "NEW")]),
            TestRecords.Decision("OLD", LifecycleStatus.Superseded, relations: [Rel(RelationKind.SupersededBy, "MID")]));

        var graph = await Get(new GovernanceGraphRequest { Focus = "OLD", Depth = 5 });

        Assert.Equal(["MID", "NEW", "OLD"], graph.Nodes.Select(n => n.Id));
        Assert.Equal(
            new[] { new GovernanceGraphEdge("MID", "Supersedes", "OLD"), new GovernanceGraphEdge("NEW", "Supersedes", "MID") },
            graph.Edges.ToArray());
        Assert.All(graph.Nodes.Where(n => n.Id != "NEW"), n => Assert.False(n.Authoritative));
    }
}
