using System.Collections.Immutable;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Domain.Catalog;
using Axiom.Infrastructure.Catalog;
using Axiom.Infrastructure.Persistence;
using Axiom.IntegrationTests.Support;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Axiom.IntegrationTests.Catalog;

/// <summary>Fluent construction of a <see cref="CatalogImport"/> as one source would report it.</summary>
internal sealed class ImportBuilder(ProvenanceSource source, string locator, DateTimeOffset observedAt)
{
    private readonly List<SoftwareEntity> _entities = [];
    private readonly List<SoftwareEdge> _edges = [];

    private EntityProvenance Provenance(double confidence = 1.0) =>
        EntityProvenance.Declared(source, locator, observedAt) with { Confidence = confidence };

    public ImportBuilder Entity(string reference, string? title = null, string[]? technologies = null, params (string Key, string Value)[] attributes)
    {
        var entityRef = EntityRef.Parse(reference);
        _entities.Add(new SoftwareEntity(
            entityRef,
            title ?? entityRef.SimpleName,
            null,
            [.. technologies ?? []],
            attributes.ToImmutableSortedDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal),
            Provenance()));
        return this;
    }

    public ImportBuilder Entities(params string[] references)
    {
        foreach (var reference in references)
        {
            Entity(reference);
        }

        return this;
    }

    public ImportBuilder Edge(string from, RelationType relation, string target, double confidence = 1.0)
    {
        _edges.Add(new SoftwareEdge(EntityRef.Parse(from), relation, EntityRef.Parse(target), Provenance(confidence)));
        return this;
    }

    public CatalogImport Build() => new(source, locator, [.. _entities], [.. _edges]);
}

/// <summary>Runs each catalog operation in its own DbContext, as separate requests would.</summary>
internal sealed class CatalogHarness(PostgresFixture postgres)
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    public FakeTimeProvider Time { get; } = new(T0);

    public CatalogOptions Options { get; } = new();

    public static EntityRef Ref(string value) => EntityRef.Parse(value);

    public static ImportBuilder Manifest(string locator = "repo:main#.axiom/catalog.yaml", DateTimeOffset? at = null) =>
        new(ProvenanceSource.RepositoryManifest, locator, at ?? T0);

    public static ImportBuilder Backstage(string locator = "backstage:backstage.example.com", DateTimeOffset? at = null) =>
        new(ProvenanceSource.Catalog, locator, at ?? T0);

    public static ImportBuilder Inferred(string locator = "scan:imports", DateTimeOffset? at = null) =>
        new(ProvenanceSource.CodeInference, locator, at ?? T0);

    public async Task<CatalogImportResult> ImportAsync(string organization, ImportBuilder import, IEventOutbox? outbox = null)
    {
        await using var db = postgres.CreateContext();
        return await new CatalogWriter(db, outbox ?? new EfEventOutbox(db), Time).ImportAsync(organization, import.Build(), CancellationToken.None);
    }

    public async Task<bool> ConfirmAsync(string organization, string from, RelationType relation, string target, string confirmedBy = "alice")
    {
        await using var db = postgres.CreateContext();
        return await new CatalogWriter(db, new EfEventOutbox(db), Time)
            .ConfirmEdgeAsync(organization, Ref(from), relation, Ref(target), confirmedBy, CancellationToken.None);
    }

    public async Task<T> QueryAsync<T>(Func<ISystemGraph, Task<T>> query)
    {
        await using var db = postgres.CreateContext();
        return await query(new PostgresSystemGraph(db, Time, Microsoft.Extensions.Options.Options.Create(Options)));
    }

    public async Task<T> WithDbAsync<T>(Func<AxiomDbContext, Task<T>> query)
    {
        await using var db = postgres.CreateContext();
        return await query(db);
    }

    public Task<ImpactResult> TraverseAsync(
        string organization, string start, TraversalDirection direction, int depth, bool includeUnconfirmed, params RelationType[] relations) =>
        QueryAsync(g => g.TraverseAsync(organization, new ImpactQuery(Ref(start), direction, [.. relations], depth, includeUnconfirmed), CancellationToken.None));

    public static IEnumerable<string> Describe(ImpactResult result) => result.Impacted.Select(i => $"{i.Depth} {i.Entity}");

    public static IEnumerable<string> Describe(ImmutableArray<PathStep> path) => path.Select(s => $"{s.From} {s.Relation} {s.To}");
}

/// <summary>Discards events; used where the outbox is not what is being measured.</summary>
internal sealed class DiscardingOutbox : IEventOutbox
{
    public void Enqueue(IntegrationEvent integrationEvent)
    {
    }
}
