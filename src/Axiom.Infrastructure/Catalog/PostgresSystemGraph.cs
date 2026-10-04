using System.Collections.Immutable;
using System.Globalization;
using Axiom.Application.Catalog;
using Axiom.Domain.Catalog;
using Axiom.Infrastructure.Catalog.Persistence;
using Axiom.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Axiom.Infrastructure.Catalog;

/// <summary>Limits of the System Graph read side, bound from <c>Axiom:Catalog</c>.</summary>
public sealed class CatalogOptions
{
    public const string SectionName = "Axiom:Catalog";

    /// <summary>Hard cap on entities returned by one traversal; more sets <c>Truncated</c>.</summary>
    public int MaxTraversalRows { get; set; } = 5000;

    /// <summary>Hard cap on nodes returned by a visualization query, whatever the caller asks for.</summary>
    public int MaxViewNodes { get; set; } = 2000;
}

/// <summary>
/// Read side of the System Graph on PostgreSQL (ADR-0004). Traversals are single, relation-specific
/// recursive queries bounded by depth and by a row cap; every query is filtered by organization.
/// </summary>
public sealed class PostgresSystemGraph(AxiomDbContext db, TimeProvider timeProvider, IOptions<CatalogOptions> options) : ISystemGraph
{
    private const string Implements = "IMPLEMENTS";
    private const string Owns = "OWNS";
    private const string Contains = "CONTAINS";
    private const string Provides = "PROVIDES";
    private const string Consumes = "CONSUMES";
    private const string DependsOn = "DEPENDS_ON";
    private const string Supports = "SUPPORTS";
    private const string StoresIn = "STORES_IN";
    private const string PublishesTo = "PUBLISHES_TO";
    private const string SubscribesTo = "SUBSCRIBES_TO";

    /// <summary>
    /// Breadth-first walk that carries only (entity, depth): the working set is bounded by entities × depth
    /// however many paths exist, and cycles end at the depth bound. The shortest depth per entity and one
    /// deterministic predecessor at that depth are then picked, from which paths are rebuilt.
    /// Parameters: 0 organization, 1 start, 2 relations, 3 max depth, 4 include unconfirmed, 5 row limit.
    /// </summary>
    private const string TraversalTemplate =
        """
        WITH RECURSIVE walk(entity_ref, depth) AS (
            SELECT {1}::text, 0
          UNION
            SELECT e.<far>::text, w.depth + 1
            FROM walk w
            JOIN axiom.software_edge e ON e.organization_id = {0} AND e.<near> = w.entity_ref
            WHERE w.depth < {3} AND e.relation = ANY({2}) AND ({4} OR e.is_fact) AND e.<far> <> {1}
        ),
        reached AS (
            SELECT entity_ref, MIN(depth) AS depth FROM walk GROUP BY entity_ref
        ),
        hops AS (
            SELECT DISTINCT ON (r.entity_ref) r.entity_ref, r.depth, p.entity_ref AS parent_ref, e.relation::text AS relation, e.is_fact
            FROM reached r
            JOIN axiom.software_edge e ON e.organization_id = {0} AND e.<far> = r.entity_ref AND e.relation = ANY({2}) AND ({4} OR e.is_fact)
            JOIN reached p ON p.entity_ref = e.<near> AND p.depth = r.depth - 1
            WHERE r.depth > 0
            ORDER BY r.entity_ref, e.is_fact DESC, e.relation COLLATE "C", p.entity_ref COLLATE "C"
        )
        SELECT entity_ref AS "EntityRef", depth AS "Depth", parent_ref AS "ParentRef", relation AS "Relation", is_fact AS "IsFact"
        FROM hops
        ORDER BY depth, entity_ref COLLATE "C"
        LIMIT {5}
        """;

    private static readonly string DownstreamSql = TraversalTemplate
        .Replace("<near>", "source_ref", StringComparison.Ordinal)
        .Replace("<far>", "target_ref", StringComparison.Ordinal);

    private static readonly string UpstreamSql = TraversalTemplate
        .Replace("<near>", "target_ref", StringComparison.Ordinal)
        .Replace("<far>", "source_ref", StringComparison.Ordinal);

    /// <summary>Undirected neighbourhood of a focus entity. Parameters: 0 organization, 1 focus, 2 depth, 3 limit.</summary>
    private const string NeighbourhoodSql =
        """
        WITH RECURSIVE hood(entity_ref, depth) AS (
            SELECT {1}::text, 0
          UNION
            SELECT n.next_ref, h.depth + 1
            FROM hood h
            CROSS JOIN LATERAL (
                SELECT e.target_ref::text AS next_ref FROM axiom.software_edge e WHERE e.organization_id = {0} AND e.source_ref = h.entity_ref
                UNION ALL
                SELECT e.source_ref::text FROM axiom.software_edge e WHERE e.organization_id = {0} AND e.target_ref = h.entity_ref
            ) n
            WHERE h.depth < {2}
        )
        SELECT h.entity_ref AS "EntityRef", MIN(h.depth) AS "Depth"
        FROM hood h
        JOIN axiom.software_entity s ON s.organization_id = {0} AND s.entity_ref = h.entity_ref
        GROUP BY h.entity_ref
        ORDER BY MIN(h.depth), h.entity_ref COLLATE "C"
        LIMIT {3}
        """;

    private static readonly ImmutableHashSet<EntityKind> ResourceKinds =
        [EntityKind.Resource, EntityKind.Database, EntityKind.Queue, EntityKind.EventStream, EntityKind.DataProduct];

    private static readonly string[] ComponentOutgoing = [Supports, Provides, Consumes, StoresIn, PublishesTo, SubscribesTo, DependsOn];

    private readonly CatalogOptions _options = options.Value;

    internal static string FormatVersion(long version) => version.ToString(CultureInfo.InvariantCulture);

    public async Task<EntityRef?> ResolveRepositoryAsync(string organizationId, string repository, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        if (string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        var text = repository.Trim();
        var bindings = db.Set<RepositoryBindingRow>().AsNoTracking().Where(b => b.OrganizationId == organizationId);
        if (EntityRef.TryParse(text, out var reference))
        {
            if (reference.Kind != EntityKind.Repository)
            {
                return null;
            }

            var name = reference.Name;
            bindings = bindings.Where(b => b.AliasType == RepositoryAliases.Name && b.Alias == name);
        }
        else
        {
            var lowered = text.ToLowerInvariant();
            var url = RepositoryUrl.Normalize(text);
            bindings = bindings.Where(b =>
                (b.AliasType == RepositoryAliases.Url && b.Alias == url) || (b.AliasType != RepositoryAliases.Url && b.Alias == lowered));
        }

        var matches = await bindings.Select(b => new { b.AliasType, b.RepositoryRef }).ToListAsync(cancellationToken).ConfigureAwait(false);

        // Only the most specific alias form counts, and it must point at exactly one repository:
        // an ambiguous name is reported as unresolved rather than guessed.
        var best = matches
            .GroupBy(m => RepositoryAliases.Priority(m.AliasType))
            .OrderBy(g => g.Key)
            .FirstOrDefault()?
            .Select(m => m.RepositoryRef)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return best is [var single] ? EntityRef.Parse(single) : null;
    }

    public async Task<SoftwareEntity?> FindAsync(string organizationId, EntityRef entity, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        var reference = entity.ToString();
        var row = await Entities(organizationId).FirstOrDefaultAsync(e => e.EntityRef == reference, cancellationToken).ConfigureAwait(false);
        return row is null ? null : CatalogMapping.ToDomain(row);
    }

    public async Task<RepositoryTopology> GetRepositoryTopologyAsync(string organizationId, EntityRef repository, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        var repositoryRef = repository.ToString();
        var gaps = new List<string>();
        var excluded = new List<SoftwareEdgeRow>();

        List<SoftwareEdgeRow> Facts(IEnumerable<SoftwareEdgeRow> edges)
        {
            var facts = new List<SoftwareEdgeRow>();
            foreach (var edge in edges)
            {
                (edge.IsFact ? facts : excluded).Add(edge);
            }

            return facts;
        }

        var implements = Facts(await Edges(organizationId)
            .Where(e => e.SourceRef == repositoryRef && e.Relation == Implements)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false));
        var components = implements.Select(e => e.TargetRef).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var nodes = components.Append(repositoryRef).ToArray();

        var described = await Entities(organizationId)
            .Where(e => nodes.Contains(e.EntityRef))
            .Select(e => new { e.EntityRef, e.Technologies })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var describedRefs = described.Select(d => d.EntityRef).ToHashSet(StringComparer.Ordinal);

        if (!describedRefs.Contains(repositoryRef))
        {
            gaps.Add($"Repository {repositoryRef} is not described in the system graph.");
        }

        if (components.Length == 0)
        {
            gaps.Add($"Repository {repositoryRef} is not bound to any component.");
        }

        var around = Facts(await Edges(organizationId)
            .Where(e => (components.Contains(e.SourceRef) && ComponentOutgoing.Contains(e.Relation)) || (nodes.Contains(e.TargetRef) && e.Relation == Owns))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false));

        // Containment is followed upward one level per query, bounded like every other traversal.
        var containment = new List<SoftwareEdgeRow>();
        var visited = new HashSet<string>(components, StringComparer.Ordinal);
        var frontier = components;
        for (var level = 0; level < ISystemGraph.MaxTraversalDepth && frontier.Length > 0; level++)
        {
            var children = frontier;
            var parents = Facts(await Edges(organizationId)
                .Where(e => e.Relation == Contains && children.Contains(e.TargetRef))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false));
            containment.AddRange(parents);
            frontier = [.. parents.Select(e => e.SourceRef).Where(visited.Add)];
        }

        var parentsOf = containment.ToLookup(e => e.TargetRef, e => EntityRef.Parse(e.SourceRef), StringComparer.Ordinal);
        var ancestors = containment.Select(e => EntityRef.Parse(e.SourceRef)).Distinct().ToArray();
        var systems = ancestors.Where(a => a.Kind == EntityKind.System).Order().ToImmutableArray();
        var domains = ancestors.Where(a => a.Kind == EntityKind.Domain).Order().ToImmutableArray();
        var owners = around.Where(e => e.Relation == Owns).ToLookup(e => e.TargetRef, e => EntityRef.Parse(e.SourceRef), StringComparer.Ordinal);

        foreach (var component in components)
        {
            if (!describedRefs.Contains(component))
            {
                gaps.Add($"Component {component} is referenced but not described in the system graph.");
            }

            if (!parentsOf[component].Any(p => p.Kind is EntityKind.System or EntityKind.Component))
            {
                gaps.Add($"Component {component} does not belong to any system.");
            }

            if (!owners[component].Any())
            {
                gaps.Add($"Component {component} has no owner.");
            }
        }

        foreach (var system in systems.Where(s => !parentsOf[s.ToString()].Any(p => p.Kind == EntityKind.Domain)))
        {
            gaps.Add($"System {system} does not belong to any domain.");
        }

        if (components.Length == 0 && !owners[repositoryRef].Any())
        {
            gaps.Add($"Repository {repositoryRef} has no owner.");
        }

        foreach (var edge in excluded.OrderBy(e => e.SourceRef, StringComparer.Ordinal).ThenBy(e => e.Relation, StringComparer.Ordinal).ThenBy(e => e.TargetRef, StringComparer.Ordinal))
        {
            gaps.Add($"Unconfirmed {edge.SourceType} relation {edge.SourceRef} {edge.Relation} {edge.TargetRef} is excluded until confirmed.");
        }

        ImmutableArray<EntityRef> Targets(Func<SoftwareEdgeRow, bool> filter) =>
            [.. around.Where(e => e.Relation != Owns).Where(filter).Select(e => EntityRef.Parse(e.TargetRef)).Distinct().Order()];

        return new RepositoryTopology(
            repository,
            [.. components.Select(EntityRef.Parse).Order()],
            systems,
            domains,
            Targets(e => e.Relation == Supports),
            Targets(e => e.Relation is Provides or Consumes),
            Targets(e => e.Relation is StoresIn or PublishesTo or SubscribesTo
                || (e.Relation == DependsOn && ResourceKinds.Contains(EntityRef.Parse(e.TargetRef).Kind))),
            [.. owners.SelectMany(g => g).Distinct().Order()],
            [.. described.SelectMany(d => d.Technologies).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)],
            [.. gaps]);
    }

    public async Task<ImpactResult> TraverseAsync(string organizationId, ImpactQuery query, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentNullException.ThrowIfNull(query);

        var start = query.Start.ToString();
        var depth = Math.Clamp(query.MaxDepth, 0, ISystemGraph.MaxTraversalDepth);
        var relations = query.Relations.IsDefaultOrEmpty
            ? []
            : query.Relations.Distinct().Select(CatalogMapping.Name).Order(StringComparer.Ordinal).ToArray();
        var cap = Math.Max(1, _options.MaxTraversalRows);
        var version = await GetCatalogVersionAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (depth == 0 || relations.Length == 0)
        {
            return new ImpactResult(query.Start, [], ImmutableDictionary<EntityRef, ImmutableArray<EntityRef>>.Empty, false, version);
        }

        var sql = query.Direction == TraversalDirection.Downstream ? DownstreamSql : UpstreamSql;
        var rows = await db.Database
            .SqlQueryRaw<TraversalRow>(sql, organizationId, start, relations, depth, query.IncludeUnconfirmed, cap + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        rows.Sort((a, b) => a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth) : string.CompareOrdinal(a.EntityRef, b.EntityRef));

        var truncated = rows.Count > cap;
        if (truncated)
        {
            rows.RemoveRange(cap, rows.Count - cap);
        }

        // Rows are ordered by depth, so every predecessor is resolved before the entities it leads to.
        var paths = new Dictionary<string, ImmutableArray<PathStep>>(StringComparer.Ordinal) { [start] = [] };
        var impacted = ImmutableArray.CreateBuilder<ImpactedEntity>(rows.Count);
        foreach (var row in rows)
        {
            var entity = EntityRef.Parse(row.EntityRef);
            var path = paths[row.ParentRef].Add(
                new PathStep(EntityRef.Parse(row.ParentRef), CatalogMapping.Relation(row.Relation), query.Direction, entity, row.IsFact));
            paths[row.EntityRef] = path;
            impacted.Add(new ImpactedEntity(entity, row.Depth, path));
        }

        var owned = paths.Keys.ToArray();
        var includeUnconfirmed = query.IncludeUnconfirmed;
        var ownerships = await Edges(organizationId)
            .Where(e => e.Relation == Owns && owned.Contains(e.TargetRef) && (includeUnconfirmed || e.IsFact))
            .Select(e => new { e.TargetRef, e.SourceRef })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var owners = ownerships
            .GroupBy(o => o.TargetRef, StringComparer.Ordinal)
            .ToImmutableDictionary(
                g => EntityRef.Parse(g.Key),
                g => g.Select(o => EntityRef.Parse(o.SourceRef)).Distinct().Order().ToImmutableArray());

        return new ImpactResult(query.Start, impacted.ToImmutable(), owners, truncated, version);
    }

    public async Task<GraphQualityReport> GetQualityReportAsync(string organizationId, TimeSpan staleAfter, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        var entities = Entities(organizationId);
        var edges = Edges(organizationId);
        var facts = edges.Where(e => e.IsFact);
        var repositoryKind = EntityRef.PrefixOf(EntityKind.Repository);
        var componentKind = EntityRef.PrefixOf(EntityKind.Component);
        var apiKind = EntityRef.PrefixOf(EntityKind.Api);
        var staleBefore = timeProvider.GetUtcNow() - staleAfter.Duration();

        var repositoriesWithoutComponent = await entities
            .Where(e => e.Kind == repositoryKind && !facts.Any(x => x.SourceRef == e.EntityRef && x.Relation == Implements))
            .Select(e => e.EntityRef).ToListAsync(cancellationToken).ConfigureAwait(false);
        var componentsWithoutOwner = await entities
            .Where(e => e.Kind == componentKind && !facts.Any(x => x.TargetRef == e.EntityRef && x.Relation == Owns))
            .Select(e => e.EntityRef).ToListAsync(cancellationToken).ConfigureAwait(false);
        var apisWithoutProvider = await entities
            .Where(e => e.Kind == apiKind && !facts.Any(x => x.TargetRef == e.EntityRef && x.Relation == Provides))
            .Select(e => e.EntityRef).ToListAsync(cancellationToken).ConfigureAwait(false);
        var consumedWithoutProvider = await facts
            .Where(c => c.Relation == Consumes && !facts.Any(p => p.TargetRef == c.TargetRef && p.Relation == Provides))
            .Select(c => c.TargetRef).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var stale = await entities.Where(e => e.LastSeen < staleBefore)
            .Select(e => e.EntityRef).ToListAsync(cancellationToken).ConfigureAwait(false);
        var unconfirmed = await edges.Where(e => !e.IsFact).ToListAsync(cancellationToken).ConfigureAwait(false);

        return new GraphQualityReport(
            Sorted(repositoriesWithoutComponent),
            Sorted(componentsWithoutOwner),
            Sorted(apisWithoutProvider),
            Sorted(consumedWithoutProvider),
            Sorted(stale),
            [.. unconfirmed.Select(CatalogMapping.ToDomain).OrderBy(e => e.From).ThenBy(e => e.Relation).ThenBy(e => e.To)],
            await entities.CountAsync(cancellationToken).ConfigureAwait(false),
            await edges.CountAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<GraphView> GetViewAsync(string organizationId, EntityRef? focus, int depth, int maxNodes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        var limit = Math.Clamp(maxNodes, 1, Math.Max(1, _options.MaxViewNodes));

        string[] refs;
        if (focus is { } center)
        {
            var hops = Math.Clamp(depth, 0, ISystemGraph.MaxTraversalDepth);
            var neighbours = await db.Database
                .SqlQueryRaw<NeighbourRow>(NeighbourhoodSql, organizationId, center.ToString(), hops, limit + 1)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            neighbours.Sort((a, b) => a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth) : string.CompareOrdinal(a.EntityRef, b.EntityRef));
            refs = [.. neighbours.Select(n => n.EntityRef)];
        }
        else
        {
            refs = await Entities(organizationId)
                .OrderBy(e => EF.Functions.Collate(e.EntityRef, "C"))
                .Select(e => e.EntityRef)
                .Take(limit + 1)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        var truncated = refs.Length > limit;
        if (truncated)
        {
            refs = refs[..limit];
        }

        var nodes = await Entities(organizationId).Where(e => refs.Contains(e.EntityRef)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var edges = await Edges(organizationId)
            .Where(e => refs.Contains(e.SourceRef) && refs.Contains(e.TargetRef))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new GraphView(
            [.. nodes.Select(CatalogMapping.ToDomain).OrderBy(n => n.Ref)],
            [.. edges.Select(CatalogMapping.ToDomain).OrderBy(e => e.From).ThenBy(e => e.Relation).ThenBy(e => e.To)],
            truncated);
    }

    public async Task<string> GetCatalogVersionAsync(string organizationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        var version = await db.Set<CatalogStateRow>().AsNoTracking()
            .Where(s => s.OrganizationId == organizationId)
            .Select(s => (long?)s.Version)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return FormatVersion(version ?? 0);
    }

    private static ImmutableArray<EntityRef> Sorted(IEnumerable<string> refs) => [.. refs.Select(EntityRef.Parse).Order()];

    private IQueryable<SoftwareEntityRow> Entities(string organizationId) =>
        db.Set<SoftwareEntityRow>().AsNoTracking().Where(e => e.OrganizationId == organizationId);

    private IQueryable<SoftwareEdgeRow> Edges(string organizationId) =>
        db.Set<SoftwareEdgeRow>().AsNoTracking().Where(e => e.OrganizationId == organizationId);

    private sealed class TraversalRow
    {
        public required string EntityRef { get; init; }

        public required int Depth { get; init; }

        public required string ParentRef { get; init; }

        public required string Relation { get; init; }

        public required bool IsFact { get; init; }
    }

    private sealed class NeighbourRow
    {
        public required string EntityRef { get; init; }

        public required int Depth { get; init; }
    }
}
