using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Domain.Catalog;
using Axiom.Infrastructure.Catalog.Persistence;
using Axiom.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Axiom.Infrastructure.Catalog;

/// <summary>
/// Write side of the System Graph. Every source locator keeps its own observations; the effective
/// node or edge is the observation of the most trusted source, so a lower-trust source can neither
/// overwrite a higher-trust one nor remove a fact another source still reports. The catalog version
/// and <c>catalog.entity.changed</c> events move only when an effective node or edge changes.
/// </summary>
public sealed class CatalogWriter(AxiomDbContext db, IEventOutbox outbox, TimeProvider timeProvider) : ICatalogWriter
{
    public const string Upserted = "upserted";
    public const string Removed = "removed";
    public const string RelationsChanged = "relations-changed";

    public async Task<CatalogImportResult> ImportAsync(string organizationId, CatalogImport import, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentNullException.ThrowIfNull(import);
        ArgumentException.ThrowIfNullOrWhiteSpace(import.SourceLocator);
        var locator = import.SourceLocator.Trim();
        if (locator.Length > CatalogColumns.LocatorLength)
        {
            throw new ArgumentException($"Source locator exceeds {CatalogColumns.LocatorLength} characters.", nameof(import));
        }

        var entities = import.Entities.IsDefault ? [] : import.Entities;
        var edges = import.Edges.IsDefault ? [] : import.Edges;
        Validate(entities, edges);

        return await InTransactionAsync(organizationId, async () =>
        {
            var changes = new ChangeSet(CatalogMapping.Name(import.Source), locator);
            await ReconcileEntitiesAsync(organizationId, entities, changes, cancellationToken).ConfigureAwait(false);
            await ReconcileEdgesAsync(organizationId, edges, changes, cancellationToken).ConfigureAwait(false);
            if (changes.Entities.Count > 0)
            {
                await SyncRepositoryBindingsAsync(organizationId, changes, cancellationToken).ConfigureAwait(false);
            }

            await PublishAsync(organizationId, changes, cancellationToken).ConfigureAwait(false);
            return new CatalogImportResult(changes.EntitiesUpserted, changes.EdgesUpserted, changes.EntitiesRemoved, changes.EdgesRemoved);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ConfirmEdgeAsync(string organizationId, EntityRef source, RelationType relation, EntityRef target, string confirmedBy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmedBy);
        var sourceRef = source.ToString();
        var targetRef = target.ToString();
        var relationName = CatalogMapping.Name(relation);

        return await InTransactionAsync(organizationId, async () =>
        {
            var edge = await db.Set<SoftwareEdgeRow>()
                .FirstOrDefaultAsync(e => e.OrganizationId == organizationId && e.SourceRef == sourceRef && e.Relation == relationName && e.TargetRef == targetRef, cancellationToken)
                .ConfigureAwait(false);
            if (edge is null)
            {
                return false;
            }

            if (edge.IsFact)
            {
                return true;
            }

            edge.ConfirmedBy = confirmedBy.Trim();
            edge.ConfirmedAt = CatalogMapping.Normalize(timeProvider.GetUtcNow());
            edge.Confirmed = true;
            edge.IsFact = true;

            var changes = new ChangeSet(edge.SourceType, edge.SourceLocator);
            changes.EdgeChanged(sourceRef, targetRef, removed: false);
            await PublishAsync(organizationId, changes, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void Validate(IEnumerable<SoftwareEntity> entities, IEnumerable<SoftwareEdge> edges)
    {
        foreach (var entity in entities)
        {
            ArgumentNullException.ThrowIfNull(entity);
            ValidateRef(entity.Ref);
            ValidateConfidence(entity.Provenance.Confidence, entity.Ref.ToString());
        }

        foreach (var edge in edges)
        {
            ArgumentNullException.ThrowIfNull(edge);
            ValidateRef(edge.From);
            ValidateRef(edge.To);
            ValidateConfidence(edge.Provenance.Confidence, $"{edge.From} {edge.Relation} {edge.To}");
        }
    }

    private static void ValidateRef(EntityRef reference)
    {
        if (reference.Name is null)
        {
            throw new ArgumentException("An import contains an uninitialized entity reference.");
        }

        if (reference.ToString().Length > CatalogColumns.RefLength)
        {
            throw new ArgumentException($"Entity reference '{reference}' exceeds {CatalogColumns.RefLength} characters.");
        }
    }

    private static void ValidateConfidence(double confidence, string subject)
    {
        if (double.IsNaN(confidence) || confidence < 0 || confidence > 1)
        {
            throw new ArgumentException($"Confidence of {subject} must be between 0 and 1.");
        }
    }

    /// <summary>
    /// Runs inside the ambient transaction, or a new one, holding a per-organization advisory lock so
    /// concurrent imports reconcile one after another instead of racing on the effective rows.
    /// </summary>
    private async Task<T> InTransactionAsync<T>(string organizationId, Func<Task<T>> work, CancellationToken cancellationToken)
    {
        var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            var lockKey = "axiom.catalog:" + organizationId;
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", cancellationToken).ConfigureAwait(false);
            var result = await work().ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ReconcileEntitiesAsync(string organizationId, IEnumerable<SoftwareEntity> entities, ChangeSet changes, CancellationToken cancellationToken)
    {
        var incoming = new Dictionary<string, SoftwareEntity>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            incoming[entity.Ref.ToString()] = entity;
        }

        var observations = db.Set<SoftwareEntityObservationRow>();
        var mine = await observations
            .Where(o => o.OrganizationId == organizationId && o.SourceLocator == changes.SourceLocator)
            .ToDictionaryAsync(o => o.EntityRef, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        var affected = mine.Keys.Union(incoming.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (affected.Length == 0)
        {
            return;
        }

        foreach (var (key, entity) in incoming)
        {
            var provenance = entity.Provenance;
            var technologies = entity.Technologies.IsDefault ? [] : entity.Technologies.ToArray();
            var attributes = CatalogMapping.SerializeAttributes(entity.Attributes ?? System.Collections.Immutable.ImmutableSortedDictionary<string, string>.Empty);
            var lastSeen = CatalogMapping.Normalize(provenance.LastSeen);
            if (mine.TryGetValue(key, out var row))
            {
                row.SourceType = changes.SourceType;
                row.Title = entity.Title;
                row.Description = entity.Description;
                row.Confidence = provenance.Confidence;
                row.Confirmed = provenance.Confirmed;
                if (!row.Technologies.AsSpan().SequenceEqual(technologies))
                {
                    row.Technologies = technologies;
                }

                if (!SameAttributes(row.Attributes, attributes))
                {
                    row.Attributes = attributes;
                }

                if (lastSeen > row.LastSeen)
                {
                    row.LastSeen = lastSeen;
                }
            }
            else
            {
                row = new SoftwareEntityObservationRow
                {
                    OrganizationId = organizationId,
                    EntityRef = key,
                    SourceLocator = changes.SourceLocator,
                    SourceType = changes.SourceType,
                    Title = entity.Title,
                    Description = entity.Description,
                    Technologies = technologies,
                    Attributes = attributes,
                    FirstSeen = CatalogMapping.Normalize(provenance.FirstSeen),
                    LastSeen = lastSeen,
                    Confidence = provenance.Confidence,
                    Confirmed = provenance.Confirmed,
                };
                observations.Add(row);
                mine[key] = row;
            }
        }

        foreach (var key in mine.Keys.Where(k => !incoming.ContainsKey(k)).ToArray())
        {
            observations.Remove(mine[key]);
            mine.Remove(key);
        }

        var others = await observations.AsNoTracking()
            .Where(o => o.OrganizationId == organizationId && o.SourceLocator != changes.SourceLocator && affected.Contains(o.EntityRef))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var effective = await db.Set<SoftwareEntityRow>()
            .Where(e => e.OrganizationId == organizationId && affected.Contains(e.EntityRef))
            .ToDictionaryAsync(e => e.EntityRef, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
        var claims = others.Concat(mine.Values).ToLookup(o => o.EntityRef, StringComparer.Ordinal);

        foreach (var key in affected)
        {
            effective.TryGetValue(key, out var row);
            var winner = claims[key]
                .OrderBy(c => (int)CatalogMapping.Source(c.SourceType))
                .ThenByDescending(c => c.Confirmed)
                .ThenByDescending(c => c.Confidence)
                .ThenBy(c => c.SourceLocator, StringComparer.Ordinal)
                .FirstOrDefault();
            if (winner is null)
            {
                if (row is not null)
                {
                    db.Remove(row);
                    changes.EntityRemoved(key);
                }

                continue;
            }

            var firstSeen = claims[key].Min(c => c.FirstSeen);
            var lastSeen = claims[key].Max(c => c.LastSeen);
            var isFact = CatalogMapping.Provenance(winner.SourceType, winner.SourceLocator, firstSeen, lastSeen, winner.Confidence, winner.Confirmed).IsFact;
            if (row is null)
            {
                var reference = EntityRef.Parse(key);
                row = new SoftwareEntityRow
                {
                    OrganizationId = organizationId,
                    EntityRef = key,
                    Kind = EntityRef.PrefixOf(reference.Kind),
                    Name = reference.Name,
                    Title = winner.Title,
                    Description = winner.Description,
                    Technologies = [.. winner.Technologies],
                    Attributes = winner.Attributes,
                    SourceType = winner.SourceType,
                    SourceLocator = winner.SourceLocator,
                    FirstSeen = firstSeen,
                    LastSeen = lastSeen,
                    Confidence = winner.Confidence,
                    Confirmed = winner.Confirmed,
                    IsFact = isFact,
                };
                db.Add(row);
                changes.EntityUpserted(key, row);
                continue;
            }

            var changed = row.Title != winner.Title
                || row.Description != winner.Description
                || !row.Technologies.AsSpan().SequenceEqual(winner.Technologies)
                || !SameAttributes(row.Attributes, winner.Attributes)
                || row.SourceType != winner.SourceType
                || row.SourceLocator != winner.SourceLocator
                || row.FirstSeen != firstSeen
                || row.Confidence != winner.Confidence
                || row.Confirmed != winner.Confirmed
                || row.IsFact != isFact;
            if (changed)
            {
                row.Title = winner.Title;
                row.Description = winner.Description;
                row.Technologies = [.. winner.Technologies];
                row.Attributes = winner.Attributes;
                row.SourceType = winner.SourceType;
                row.SourceLocator = winner.SourceLocator;
                row.FirstSeen = firstSeen;
                row.Confidence = winner.Confidence;
                row.Confirmed = winner.Confirmed;
                row.IsFact = isFact;
                changes.EntityUpserted(key, row);
            }

            // Being seen again is not a change of the graph: it moves no version and emits no event.
            if (row.LastSeen != lastSeen)
            {
                row.LastSeen = lastSeen;
            }
        }
    }

    private async Task ReconcileEdgesAsync(string organizationId, IEnumerable<SoftwareEdge> edges, ChangeSet changes, CancellationToken cancellationToken)
    {
        var incoming = new Dictionary<EdgeKey, SoftwareEdge>();
        foreach (var edge in edges)
        {
            incoming[new EdgeKey(edge.From.ToString(), CatalogMapping.Name(edge.Relation), edge.To.ToString())] = edge;
        }

        var observations = db.Set<SoftwareEdgeObservationRow>();
        var mine = (await observations
                .Where(o => o.OrganizationId == organizationId && o.SourceLocator == changes.SourceLocator)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(o => new EdgeKey(o.SourceRef, o.Relation, o.TargetRef));

        var affected = mine.Keys.Union(incoming.Keys).Order().ToArray();
        if (affected.Length == 0)
        {
            return;
        }

        foreach (var (key, edge) in incoming)
        {
            var provenance = edge.Provenance;
            var lastSeen = CatalogMapping.Normalize(provenance.LastSeen);
            if (mine.TryGetValue(key, out var row))
            {
                row.SourceType = changes.SourceType;
                row.Confidence = provenance.Confidence;
                row.Confirmed = provenance.Confirmed;
                if (lastSeen > row.LastSeen)
                {
                    row.LastSeen = lastSeen;
                }
            }
            else
            {
                row = new SoftwareEdgeObservationRow
                {
                    OrganizationId = organizationId,
                    SourceRef = key.Source,
                    Relation = key.Relation,
                    TargetRef = key.Target,
                    SourceLocator = changes.SourceLocator,
                    SourceType = changes.SourceType,
                    FirstSeen = CatalogMapping.Normalize(provenance.FirstSeen),
                    LastSeen = lastSeen,
                    Confidence = provenance.Confidence,
                    Confirmed = provenance.Confirmed,
                };
                observations.Add(row);
                mine[key] = row;
            }
        }

        foreach (var key in mine.Keys.Where(k => !incoming.ContainsKey(k)).ToArray())
        {
            observations.Remove(mine[key]);
            mine.Remove(key);
        }

        // Rows are fetched by source entity (index prefix) and narrowed to the affected edges in memory.
        var affectedKeys = affected.ToHashSet();
        var sources = affected.Select(k => k.Source).Distinct(StringComparer.Ordinal).ToArray();
        var others = (await observations.AsNoTracking()
                .Where(o => o.OrganizationId == organizationId && o.SourceLocator != changes.SourceLocator && sources.Contains(o.SourceRef))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Where(o => affectedKeys.Contains(new EdgeKey(o.SourceRef, o.Relation, o.TargetRef)));
        var effective = (await db.Set<SoftwareEdgeRow>()
                .Where(e => e.OrganizationId == organizationId && sources.Contains(e.SourceRef))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(e => new EdgeKey(e.SourceRef, e.Relation, e.TargetRef));
        var claims = others.Concat(mine.Values).ToLookup(o => new EdgeKey(o.SourceRef, o.Relation, o.TargetRef));

        foreach (var key in affected)
        {
            effective.TryGetValue(key, out var row);
            var winner = claims[key]
                .OrderBy(c => (int)CatalogMapping.Source(c.SourceType))
                .ThenByDescending(c => c.Confirmed)
                .ThenByDescending(c => c.Confidence)
                .ThenBy(c => c.SourceLocator, StringComparer.Ordinal)
                .FirstOrDefault();
            if (winner is null)
            {
                if (row is not null)
                {
                    db.Remove(row);
                    changes.EdgeChanged(key.Source, key.Target, removed: true);
                }

                continue;
            }

            var firstSeen = claims[key].Min(c => c.FirstSeen);
            var lastSeen = claims[key].Max(c => c.LastSeen);
            var confirmed = winner.Confirmed || row?.ConfirmedBy is not null;
            var isFact = CatalogMapping.Provenance(winner.SourceType, winner.SourceLocator, firstSeen, lastSeen, winner.Confidence, confirmed).IsFact;
            if (row is null)
            {
                db.Add(new SoftwareEdgeRow
                {
                    OrganizationId = organizationId,
                    SourceRef = key.Source,
                    Relation = key.Relation,
                    TargetRef = key.Target,
                    SourceType = winner.SourceType,
                    SourceLocator = winner.SourceLocator,
                    FirstSeen = firstSeen,
                    LastSeen = lastSeen,
                    Confidence = winner.Confidence,
                    Confirmed = confirmed,
                    IsFact = isFact,
                });
                changes.EdgeChanged(key.Source, key.Target, removed: false);
                continue;
            }

            var changed = row.SourceType != winner.SourceType
                || row.SourceLocator != winner.SourceLocator
                || row.FirstSeen != firstSeen
                || row.Confidence != winner.Confidence
                || row.Confirmed != confirmed
                || row.IsFact != isFact;
            if (changed)
            {
                row.SourceType = winner.SourceType;
                row.SourceLocator = winner.SourceLocator;
                row.FirstSeen = firstSeen;
                row.Confidence = winner.Confidence;
                row.Confirmed = confirmed;
                row.IsFact = isFact;
                changes.EdgeChanged(key.Source, key.Target, removed: false);
            }

            if (row.LastSeen != lastSeen)
            {
                row.LastSeen = lastSeen;
            }
        }
    }

    /// <summary>Rebuilds the aliases of every repository entity whose effective node changed.</summary>
    private async Task SyncRepositoryBindingsAsync(string organizationId, ChangeSet changes, CancellationToken cancellationToken)
    {
        var prefix = EntityRef.PrefixOf(EntityKind.Repository) + ":";
        var repositories = changes.Entities.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (repositories.Length == 0)
        {
            return;
        }

        var refs = repositories.Select(kv => kv.Key).ToArray();
        var bindings = db.Set<RepositoryBindingRow>();
        var existing = (await bindings
                .Where(b => b.OrganizationId == organizationId && refs.Contains(b.RepositoryRef))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToLookup(b => b.RepositoryRef, StringComparer.Ordinal);

        foreach (var (reference, row) in repositories)
        {
            var desired = row is null
                ? []
                : RepositoryAliases.For(EntityRef.Parse(reference), CatalogMapping.DeserializeAttributes(row.Attributes));
            foreach (var binding in existing[reference])
            {
                if (!desired.Remove((binding.Alias, binding.AliasType)))
                {
                    bindings.Remove(binding);
                }
            }

            foreach (var (alias, type) in desired.Where(d => d.Alias.Length <= CatalogColumns.LocatorLength))
            {
                bindings.Add(new RepositoryBindingRow { OrganizationId = organizationId, Alias = alias, AliasType = type, RepositoryRef = reference });
            }
        }
    }

    /// <summary>Advances the catalog version and queues one event per affected entity, only if something changed.</summary>
    private async Task PublishAsync(string organizationId, ChangeSet changes, CancellationToken cancellationToken)
    {
        if (!changes.Any)
        {
            return;
        }

        var now = CatalogMapping.Normalize(timeProvider.GetUtcNow());
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO axiom.catalog_state (organization_id, version, updated_at) VALUES ({organizationId}, 1, {now})
             ON CONFLICT (organization_id) DO UPDATE SET version = axiom.catalog_state.version + 1, updated_at = EXCLUDED.updated_at
             """,
            cancellationToken).ConfigureAwait(false);
        var version = await db.Set<CatalogStateRow>().AsNoTracking()
            .Where(s => s.OrganizationId == organizationId)
            .Select(s => s.Version)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);
        var catalogVersion = PostgresSystemGraph.FormatVersion(version);

        var events = changes.Related.Select(reference => (Reference: reference, Change: RelationsChanged))
            .Where(e => !changes.Entities.ContainsKey(e.Reference))
            .Concat(changes.Entities.Select(kv => (Reference: kv.Key, Change: kv.Value is null ? Removed : Upserted)))
            .OrderBy(e => e.Reference, StringComparer.Ordinal);
        foreach (var (reference, change) in events)
        {
            outbox.Enqueue(IntegrationEvent.Create(
                EventTypes.CatalogEntityChanged,
                organizationId,
                now,
                $"{reference}|{change}|{catalogVersion}",
                new CatalogEntityChanged(reference, change, new CatalogChangeProvenance(changes.SourceType, changes.SourceLocator), catalogVersion)));
        }
    }

    private static bool SameAttributes(string left, string right) =>
        left == right || CatalogMapping.DeserializeAttributes(left).SequenceEqual(CatalogMapping.DeserializeAttributes(right));

    private readonly record struct EdgeKey(string Source, string Relation, string Target) : IComparable<EdgeKey>
    {
        public int CompareTo(EdgeKey other)
        {
            var bySource = string.CompareOrdinal(Source, other.Source);
            if (bySource != 0)
            {
                return bySource;
            }

            var byRelation = string.CompareOrdinal(Relation, other.Relation);
            return byRelation != 0 ? byRelation : string.CompareOrdinal(Target, other.Target);
        }
    }

    /// <summary>Effective changes made by one write, which decide the version bump and the events.</summary>
    private sealed class ChangeSet(string sourceType, string sourceLocator)
    {
        public string SourceType { get; } = sourceType;

        public string SourceLocator { get; } = sourceLocator;

        /// <summary>Changed entities; the value is the new effective row, or null when the entity was removed.</summary>
        public Dictionary<string, SoftwareEntityRow?> Entities { get; } = new(StringComparer.Ordinal);

        /// <summary>Endpoints of changed edges.</summary>
        public HashSet<string> Related { get; } = new(StringComparer.Ordinal);

        public int EntitiesUpserted { get; private set; }

        public int EntitiesRemoved { get; private set; }

        public int EdgesUpserted { get; private set; }

        public int EdgesRemoved { get; private set; }

        public bool Any => Entities.Count > 0 || Related.Count > 0;

        public void EntityUpserted(string reference, SoftwareEntityRow row)
        {
            Entities[reference] = row;
            EntitiesUpserted++;
        }

        public void EntityRemoved(string reference)
        {
            Entities[reference] = null;
            EntitiesRemoved++;
        }

        public void EdgeChanged(string source, string target, bool removed)
        {
            Related.Add(source);
            Related.Add(target);
            if (removed)
            {
                EdgesRemoved++;
            }
            else
            {
                EdgesUpserted++;
            }
        }
    }
}

/// <summary>Payload of <c>catalog.entity.changed</c> (docs/04-contracts/event-contracts.md).</summary>
public sealed record CatalogEntityChanged(string EntityRef, string ChangeType, CatalogChangeProvenance Provenance, string CatalogVersion);

/// <summary>The source whose observation caused a catalog change.</summary>
public sealed record CatalogChangeProvenance(string SourceType, string SourceLocator);
