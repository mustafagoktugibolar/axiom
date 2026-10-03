using System.Collections.Immutable;

namespace Axiom.Domain.Catalog;

/// <summary>Typed relations of the System Graph (design.md §4.3 plus system-graph.md).</summary>
public enum RelationType
{
    Contains,
    Owns,
    Implements,
    Provides,
    Consumes,
    DependsOn,
    PublishesTo,
    SubscribesTo,
    StoresIn,
    DeployedAs,
    Supports,
    GovernedBy,
}

/// <summary>Where a node or edge was learned from, in descending default trust.</summary>
public enum ProvenanceSource
{
    Catalog,
    RepositoryManifest,
    DeploymentDiscovery,
    Manual,
    CodeInference,
    LlmInference,
}

/// <summary>
/// Provenance carried by every node and edge. Inferred facts stay candidates until a human confirms
/// them (R4: missing or inferred topology is never silently treated as fact).
/// </summary>
public sealed record EntityProvenance(
    ProvenanceSource Source,
    string Locator,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    double Confidence,
    bool Confirmed)
{
    /// <summary>Declared sources are trusted as facts; inferred ones only once confirmed.</summary>
    public bool IsFact => Confirmed || Source is ProvenanceSource.Catalog or ProvenanceSource.RepositoryManifest or ProvenanceSource.Manual;

    public static EntityProvenance Declared(ProvenanceSource source, string locator, DateTimeOffset seenAt) =>
        new(source, locator, seenAt, seenAt, 1.0, Confirmed: source is not (ProvenanceSource.CodeInference or ProvenanceSource.LlmInference));
}

public sealed record SoftwareEntity(
    EntityRef Ref,
    string Title,
    string? Description,
    ImmutableArray<string> Technologies,
    ImmutableSortedDictionary<string, string> Attributes,
    EntityProvenance Provenance);

public sealed record SoftwareEdge(EntityRef From, RelationType Relation, EntityRef To, EntityProvenance Provenance);

/// <summary>Direction of traversal relative to the stored edge direction.</summary>
public enum TraversalDirection
{
    /// <summary>Follow edges from source to target: "what does this depend on".</summary>
    Downstream,

    /// <summary>Follow edges from target to source: "what depends on this".</summary>
    Upstream,
}

/// <summary>One hop in an impact path.</summary>
public sealed record PathStep(EntityRef From, RelationType Relation, TraversalDirection Direction, EntityRef To, bool IsFact);

/// <summary>An entity reached by a bounded traversal together with the shortest path that reached it.</summary>
public sealed record ImpactedEntity(EntityRef Entity, int Depth, ImmutableArray<PathStep> Path)
{
    /// <summary>An impact is only as certain as its least certain hop.</summary>
    public bool IsFact => Path.All(step => step.IsFact);
}
