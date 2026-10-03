using System.Collections.Immutable;
using Axiom.Domain.Catalog;

namespace Axiom.Application.Catalog;

/// <summary>The chain of entities a repository belongs to. Missing links are gaps, never guesses (R4).</summary>
public sealed record RepositoryTopology(
    EntityRef Repository,
    ImmutableArray<EntityRef> Components,
    ImmutableArray<EntityRef> Systems,
    ImmutableArray<EntityRef> Domains,
    ImmutableArray<EntityRef> Capabilities,
    ImmutableArray<EntityRef> Apis,
    ImmutableArray<EntityRef> Resources,
    ImmutableArray<EntityRef> Owners,
    ImmutableArray<string> Technologies,
    ImmutableArray<string> Gaps);

public sealed record ImpactQuery(
    EntityRef Start,
    TraversalDirection Direction,
    ImmutableArray<RelationType> Relations,
    int MaxDepth,
    bool IncludeUnconfirmed);

public sealed record ImpactResult(
    EntityRef Start,
    ImmutableArray<ImpactedEntity> Impacted,
    ImmutableDictionary<EntityRef, ImmutableArray<EntityRef>> Owners,
    bool Truncated,
    string CatalogVersion);

public sealed record GraphQualityReport(
    ImmutableArray<EntityRef> RepositoriesWithoutComponent,
    ImmutableArray<EntityRef> ComponentsWithoutOwner,
    ImmutableArray<EntityRef> ApisWithoutProvider,
    ImmutableArray<EntityRef> ConsumedApisWithoutProvider,
    ImmutableArray<EntityRef> StaleEntities,
    ImmutableArray<SoftwareEdge> UnconfirmedEdges,
    int EntityCount,
    int EdgeCount);

public sealed record GraphView(ImmutableArray<SoftwareEntity> Nodes, ImmutableArray<SoftwareEdge> Edges, bool Truncated);

/// <summary>Read side of the System Graph (ADR-0004: PostgreSQL with bounded recursive queries).</summary>
public interface ISystemGraph
{
    public const int MaxTraversalDepth = 6;

    /// <summary>Resolves a repository by name, namespaced name, reference, or clone URL. Null when unbound.</summary>
    Task<EntityRef?> ResolveRepositoryAsync(string organizationId, string repository, CancellationToken cancellationToken);

    Task<SoftwareEntity?> FindAsync(string organizationId, EntityRef entity, CancellationToken cancellationToken);

    Task<RepositoryTopology> GetRepositoryTopologyAsync(string organizationId, EntityRef repository, CancellationToken cancellationToken);

    /// <summary>Bounded, relation-specific traversal. Depth is clamped to <see cref="MaxTraversalDepth"/>.</summary>
    Task<ImpactResult> TraverseAsync(string organizationId, ImpactQuery query, CancellationToken cancellationToken);

    Task<GraphQualityReport> GetQualityReportAsync(string organizationId, TimeSpan staleAfter, CancellationToken cancellationToken);

    /// <summary>Nodes and edges for visualization, optionally limited to the neighbourhood of one entity.</summary>
    Task<GraphView> GetViewAsync(string organizationId, EntityRef? focus, int depth, int maxNodes, CancellationToken cancellationToken);

    /// <summary>Monotonic version of the organization's catalog; changes whenever any node or edge changes.</summary>
    Task<string> GetCatalogVersionAsync(string organizationId, CancellationToken cancellationToken);
}

/// <summary>A set of entities and edges observed from one source at one time.</summary>
public sealed record CatalogImport(
    ProvenanceSource Source,
    string SourceLocator,
    ImmutableArray<SoftwareEntity> Entities,
    ImmutableArray<SoftwareEdge> Edges);

public sealed record CatalogImportResult(int EntitiesUpserted, int EdgesUpserted, int EntitiesRemoved, int EdgesRemoved);

/// <summary>Write side of the System Graph.</summary>
public interface ICatalogWriter
{
    /// <summary>
    /// Upserts what the source observed and removes what the same source locator previously reported but
    /// no longer does. Facts from other sources are untouched. Idempotent.
    /// </summary>
    Task<CatalogImportResult> ImportAsync(string organizationId, CatalogImport import, CancellationToken cancellationToken);

    /// <summary>Marks an inferred entity or edge as human-confirmed fact.</summary>
    Task<bool> ConfirmEdgeAsync(string organizationId, EntityRef source, RelationType relation, EntityRef target, string confirmedBy, CancellationToken cancellationToken);
}

/// <summary>Turns a repository's catalog manifest (<c>catalog-info.yaml</c> / <c>.axiom/catalog.yaml</c>) into graph facts.</summary>
public interface ICatalogManifestParser
{
    /// <summary>File names recognized as catalog manifests, relative to the repository root.</summary>
    IReadOnlyList<string> ManifestPaths { get; }

    CatalogManifestResult Parse(string organizationId, string sourceLocator, string path, string content, DateTimeOffset observedAt);
}

public sealed record CatalogManifestResult(CatalogImport? Import, ImmutableArray<string> Errors);

/// <summary>Optional Backstage catalog adapter (task 2.8). Backstage stays the owner of catalog fields.</summary>
public interface IBackstageCatalogClient
{
    Task<CatalogImport> FetchAsync(string organizationId, DateTimeOffset observedAt, CancellationToken cancellationToken);
}
