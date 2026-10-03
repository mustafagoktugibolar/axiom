using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Domain.Catalog;
using Axiom.Infrastructure.Catalog.Persistence;

namespace Axiom.Infrastructure.Catalog;

/// <summary>Stable storage names for relations and provenance sources, and row ↔ domain conversions.</summary>
internal static class CatalogMapping
{
    private static readonly ImmutableDictionary<RelationType, string> RelationNames = new Dictionary<RelationType, string>
    {
        [RelationType.Contains] = "CONTAINS",
        [RelationType.Owns] = "OWNS",
        [RelationType.Implements] = "IMPLEMENTS",
        [RelationType.Provides] = "PROVIDES",
        [RelationType.Consumes] = "CONSUMES",
        [RelationType.DependsOn] = "DEPENDS_ON",
        [RelationType.PublishesTo] = "PUBLISHES_TO",
        [RelationType.SubscribesTo] = "SUBSCRIBES_TO",
        [RelationType.StoresIn] = "STORES_IN",
        [RelationType.DeployedAs] = "DEPLOYED_AS",
        [RelationType.Supports] = "SUPPORTS",
        [RelationType.GovernedBy] = "GOVERNED_BY",
    }.ToImmutableDictionary();

    private static readonly ImmutableDictionary<string, RelationType> Relations =
        RelationNames.ToImmutableDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    private static readonly ImmutableDictionary<ProvenanceSource, string> SourceNames = new Dictionary<ProvenanceSource, string>
    {
        [ProvenanceSource.Catalog] = "catalog",
        [ProvenanceSource.RepositoryManifest] = "repository-manifest",
        [ProvenanceSource.DeploymentDiscovery] = "deployment-discovery",
        [ProvenanceSource.Manual] = "manual",
        [ProvenanceSource.CodeInference] = "code-inference",
        [ProvenanceSource.LlmInference] = "llm-inference",
    }.ToImmutableDictionary();

    private static readonly ImmutableDictionary<string, ProvenanceSource> Sources =
        SourceNames.ToImmutableDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    public static string Name(RelationType relation) => RelationNames[relation];

    public static RelationType Relation(string name) => Relations[name];

    public static string Name(ProvenanceSource source) => SourceNames[source];

    public static ProvenanceSource Source(string name) => Sources[name];

    /// <summary>PostgreSQL keeps microseconds; truncating up front keeps comparisons stable across round trips.</summary>
    public static DateTimeOffset Normalize(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.AddTicks(-(utc.Ticks % 10));
    }

    public static string SerializeAttributes(IEnumerable<KeyValuePair<string, string>> attributes) =>
        JsonSerializer.Serialize(new SortedDictionary<string, string>(attributes.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal), StringComparer.Ordinal));

    public static ImmutableSortedDictionary<string, string> DeserializeAttributes(string json) =>
        (JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []).ToImmutableSortedDictionary(StringComparer.Ordinal);

    public static EntityProvenance Provenance(string sourceType, string locator, DateTimeOffset firstSeen, DateTimeOffset lastSeen, double confidence, bool confirmed) =>
        new(Source(sourceType), locator, firstSeen, lastSeen, confidence, confirmed);

    public static SoftwareEntity ToDomain(SoftwareEntityRow row) => new(
        EntityRef.Parse(row.EntityRef),
        row.Title,
        row.Description,
        [.. row.Technologies],
        DeserializeAttributes(row.Attributes),
        Provenance(row.SourceType, row.SourceLocator, row.FirstSeen, row.LastSeen, row.Confidence, row.Confirmed));

    public static SoftwareEdge ToDomain(SoftwareEdgeRow row) => new(
        EntityRef.Parse(row.SourceRef),
        Relation(row.Relation),
        EntityRef.Parse(row.TargetRef),
        Provenance(row.SourceType, row.SourceLocator, row.FirstSeen, row.LastSeen, row.Confidence, row.Confirmed));
}
