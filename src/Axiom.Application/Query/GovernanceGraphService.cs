using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Application.Governance;
using Axiom.Domain.Governance;

namespace Axiom.Application.Query;

public sealed record GovernanceGraphRequest
{
    public string? Focus { get; init; }

    public int? Depth { get; init; }

    public int? MaxNodes { get; init; }

    public int? MaxEdges { get; init; }

    public ImmutableArray<RecordKind> Kinds { get; init; } = [];

    public ImmutableArray<LifecycleStatus> Statuses { get; init; } = [];

    /// <summary>Records that restrict this dimension to this value (same meaning as the search filter).</summary>
    public ImmutableDictionary<ScopeDimension, string> Scope { get; init; } = ImmutableDictionary<ScopeDimension, string>.Empty;
}

public sealed record GovernanceGraphNode(string Id, string Kind, string Title, string Status, bool Authoritative, string AuthorityLevel, bool Focus);

/// <summary>A directed relation. SupersededBy is reported as the equivalent Supersedes edge in the opposite direction.</summary>
public sealed record GovernanceGraphEdge(string From, string Relation, string To);

public sealed record GovernanceGraphDto(
    ImmutableArray<GovernanceGraphNode> Nodes,
    ImmutableArray<GovernanceGraphEdge> Edges,
    bool Truncated,
    string? Focus,
    int Depth);

/// <summary>Records as nodes and their relations as edges (supersedes, refines, conflictsWith, related, ...), bounded in size.</summary>
public sealed class GovernanceGraphService(IGovernanceSnapshotProvider snapshots, TimeProvider time)
{
    public const int DefaultDepth = 2;
    public const int MaxDepth = 5;
    public const int DefaultMaxNodes = 100;
    public const int MaxNodesLimit = 300;
    public const int DefaultMaxEdges = 400;
    public const int MaxEdgesLimit = 1000;

    public async Task<GovernanceGraphDto> GetAsync(AxiomPrincipal principal, GovernanceGraphRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        Authorizer.Demand(principal, AccessRight.ReadGovernance);

        var depth = Math.Clamp(request.Depth ?? DefaultDepth, 1, MaxDepth);
        var maxNodes = Math.Clamp(request.MaxNodes ?? DefaultMaxNodes, 1, MaxNodesLimit);
        var maxEdges = Math.Clamp(request.MaxEdges ?? DefaultMaxEdges, 1, MaxEdgesLimit);
        var focus = string.IsNullOrWhiteSpace(request.Focus) ? null : request.Focus.Trim();
        if (focus is { Length: > 128 })
        {
            throw AxiomException.Invalid("'focus' is not a valid governance record ID.");
        }

        var snapshot = await snapshots.GetCurrentAsync(principal.OrganizationId, cancellationToken);
        if (snapshot is null)
        {
            return focus is null ? new GovernanceGraphDto([], [], false, null, depth) : throw AxiomException.NotFound($"Governance record '{focus}'");
        }

        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var all = new List<RecordRevision>(snapshot.Revisions);
        var edgesAll = all
            .SelectMany(r => r.Record.Relations.Select(x => Normalize(r.Id, x)))
            .Where(e => snapshot.Find(e.From) is not null && snapshot.Find(e.To) is not null)
            .Distinct()
            .OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.Relation, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal)
            .ToList();

        bool Matches(RecordRevision r) =>
            (request.Kinds.IsDefaultOrEmpty || request.Kinds.Contains(r.Record.Kind))
            && (request.Statuses.IsDefaultOrEmpty || request.Statuses.Contains(r.Record.Status))
            && request.Scope.All(s => r.Record.Scope.Restricts(s.Key)
                && r.Record.Scope[s.Key].Contains(s.Value, Domain.Governance.Scope.ComparerFor(s.Key)));

        var chosen = new List<string>();
        var truncated = false;
        if (focus is not null)
        {
            if (snapshot.Find(focus) is null)
            {
                throw AxiomException.NotFound($"Governance record '{focus}'");
            }

            var neighbours = edgesAll.SelectMany(e => new[] { (e.From, e.To), (e.To, e.From) }).ToLookup(p => p.Item1, p => p.Item2);
            var seen = new HashSet<string>(StringComparer.Ordinal) { focus };
            chosen.Add(focus);
            var frontier = new List<string> { focus };
            for (var level = 0; level < depth && frontier.Count > 0; level++)
            {
                var next = new List<string>();
                foreach (var id in frontier)
                {
                    foreach (var other in neighbours[id].Distinct().Order(StringComparer.Ordinal))
                    {
                        if (seen.Contains(other) || !Matches(snapshot.Find(other)!))
                        {
                            continue;
                        }

                        if (chosen.Count >= maxNodes)
                        {
                            truncated = true;
                            continue;
                        }

                        seen.Add(other);
                        chosen.Add(other);
                        next.Add(other);
                    }
                }

                frontier = next;
            }
        }
        else
        {
            var matching = all.Where(Matches).Select(r => r.Id).Order(StringComparer.Ordinal).ToList();
            truncated = matching.Count > maxNodes;
            chosen.AddRange(matching.Take(maxNodes));
        }

        var inGraph = chosen.ToHashSet(StringComparer.Ordinal);
        var edges = edgesAll.Where(e => inGraph.Contains(e.From) && inGraph.Contains(e.To)).ToList();
        if (edges.Count > maxEdges)
        {
            truncated = true;
            edges = edges.Take(maxEdges).ToList();
        }

        var nodes = chosen
            .Select(id => snapshot.Find(id)!.Record)
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .Select(r => new GovernanceGraphNode(r.Id, r.Kind.ToString(), r.Title, r.Status.ToString(), r.IsAuthoritativeOn(today), r.Authority.Level.ToString(), string.Equals(r.Id, focus, StringComparison.Ordinal)));
        return new GovernanceGraphDto([.. nodes], [.. edges], truncated, focus, depth);
    }

    private static GovernanceGraphEdge Normalize(string owner, Relation relation) =>
        relation.Kind == RelationKind.SupersededBy
            ? new GovernanceGraphEdge(relation.TargetId, nameof(RelationKind.Supersedes), owner)
            : new GovernanceGraphEdge(owner, relation.Kind.ToString(), relation.TargetId);
}
