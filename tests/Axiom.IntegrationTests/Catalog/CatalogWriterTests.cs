using System.Text.Json;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Domain.Catalog;
using Axiom.Infrastructure.Catalog.Persistence;
using Axiom.Infrastructure.Persistence;
using Axiom.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using static Axiom.IntegrationTests.Catalog.CatalogHarness;

namespace Axiom.IntegrationTests.Catalog;

[Collection(PostgresTests.Name)]
public class CatalogWriterTests(PostgresFixture postgres)
{
    private readonly CatalogHarness _catalog = new(postgres);
    private readonly string _org = PostgresFixture.NewOrganization();

    private static ImportBuilder Gateway(ImportBuilder builder) => builder
        .Entity("component:gateway", "Gateway", ["go"])
        .Entity("repo:gateway")
        .Entity("team:platform")
        .Edge("repo:gateway", RelationType.Implements, "component:gateway")
        .Edge("team:platform", RelationType.Owns, "component:gateway");

    private Task<string> VersionAsync() => _catalog.QueryAsync(g => g.GetCatalogVersionAsync(_org, CancellationToken.None));

    private Task<List<OutboxEvent>> EventsAsync() => _catalog.WithDbAsync(db => db.Set<OutboxEvent>()
        .Where(e => e.OrganizationId == _org && e.EventType == EventTypes.CatalogEntityChanged)
        .ToListAsync());

    [Fact]
    public async Task Import_is_idempotent_and_moves_the_version_only_on_change()
    {
        Assert.Equal("0", await VersionAsync());

        var first = await _catalog.ImportAsync(_org, Gateway(Manifest()));
        Assert.Equal(new CatalogImportResult(3, 2, 0, 0), first);
        Assert.Equal("1", await VersionAsync());
        var eventsAfterFirst = await EventsAsync();
        Assert.Equal(3, eventsAfterFirst.Count);

        var again = await _catalog.ImportAsync(_org, Gateway(Manifest()));
        Assert.Equal(new CatalogImportResult(0, 0, 0, 0), again);
        Assert.Equal("1", await VersionAsync());
        Assert.Equal(3, (await EventsAsync()).Count);

        // Seen again later: nothing about the graph changed, so no version bump and no events,
        // but first_seen is preserved and last_seen moves.
        var later = T0.AddDays(2);
        var reobserved = await _catalog.ImportAsync(_org, Gateway(Manifest(at: later)));
        Assert.Equal(new CatalogImportResult(0, 0, 0, 0), reobserved);
        Assert.Equal("1", await VersionAsync());
        Assert.Equal(3, (await EventsAsync()).Count);

        var entity = await _catalog.QueryAsync(g => g.FindAsync(_org, Ref("component:gateway"), CancellationToken.None));
        Assert.NotNull(entity);
        Assert.Equal("Gateway", entity.Title);
        Assert.Equal(["go"], entity.Technologies.AsEnumerable());
        Assert.Equal(T0, entity.Provenance.FirstSeen);
        Assert.Equal(later, entity.Provenance.LastSeen);
        Assert.Equal(ProvenanceSource.RepositoryManifest, entity.Provenance.Source);
        Assert.True(entity.Provenance.IsFact);

        var edge = await _catalog.WithDbAsync(db => db.Set<SoftwareEdgeRow>().SingleAsync(e => e.OrganizationId == _org && e.Relation == "OWNS"));
        Assert.Equal(T0, edge.FirstSeen);
        Assert.Equal(later, edge.LastSeen);
    }

    [Fact]
    public async Task A_changed_attribute_is_one_upsert_and_one_new_version()
    {
        await _catalog.ImportAsync(_org, Gateway(Manifest()));

        var changed = await _catalog.ImportAsync(_org, Manifest()
            .Entity("component:gateway", "Edge Gateway", ["go"])
            .Entity("repo:gateway")
            .Entity("team:platform")
            .Edge("repo:gateway", RelationType.Implements, "component:gateway")
            .Edge("team:platform", RelationType.Owns, "component:gateway"));

        Assert.Equal(new CatalogImportResult(1, 0, 0, 0), changed);
        Assert.Equal("2", await VersionAsync());
        Assert.Equal("Edge Gateway", (await _catalog.QueryAsync(g => g.FindAsync(_org, Ref("component:gateway"), CancellationToken.None)))!.Title);
    }

    [Fact]
    public async Task Events_carry_entity_change_type_provenance_and_version()
    {
        await _catalog.ImportAsync(_org, Gateway(Manifest()));
        await _catalog.ImportAsync(_org, Manifest().Entity("component:gateway", "Gateway", ["go"]).Entity("team:platform"));

        var events = (await EventsAsync())
            .Select(e => JsonDocument.Parse(e.Data).RootElement)
            .Select(d => (
                Entity: d.GetProperty("entityRef").GetString(),
                Change: d.GetProperty("changeType").GetString(),
                Version: d.GetProperty("catalogVersion").GetString(),
                Source: d.GetProperty("provenance").GetProperty("sourceType").GetString(),
                Locator: d.GetProperty("provenance").GetProperty("sourceLocator").GetString()))
            .OrderBy(e => e.Version).ThenBy(e => e.Entity)
            .ToList();

        Assert.Equal(
            [
                ("component:gateway", "upserted", "1"),
                ("repo:gateway", "upserted", "1"),
                ("team:platform", "upserted", "1"),
                // Second import: the repository and both edges are gone.
                ("component:gateway", "relations-changed", "2"),
                ("repo:gateway", "removed", "2"),
                ("team:platform", "relations-changed", "2"),
            ],
            events.Select(e => (e.Entity!, e.Change!, e.Version!)));
        Assert.All(events, e =>
        {
            Assert.Equal("repository-manifest", e.Source);
            Assert.Equal("repo:main#.axiom/catalog.yaml", e.Locator);
        });
        Assert.Equal(events.Count, (await EventsAsync()).Select(e => e.EventId).Distinct().Count());
    }

    [Fact]
    public async Task A_source_removes_only_what_it_reported_itself()
    {
        await _catalog.ImportAsync(_org, Manifest("repo:a#catalog")
            .Entities("component:a", "component:shared")
            .Edge("component:a", RelationType.DependsOn, "component:shared")
            .Edge("component:a", RelationType.Consumes, "api:x"));
        await _catalog.ImportAsync(_org, Manifest("repo:b#catalog")
            .Entities("component:b", "component:shared")
            .Edge("component:b", RelationType.DependsOn, "component:shared")
            .Edge("component:a", RelationType.Consumes, "api:x"));

        // Source A now reports only component:a and no edges.
        var result = await _catalog.ImportAsync(_org, Manifest("repo:a#catalog").Entities("component:a"));

        // component:shared and the Consumes edge pass to source B (two upserts); A's own DependsOn edge goes.
        Assert.Equal(new CatalogImportResult(1, 1, 0, 1), result);
        var view = await _catalog.QueryAsync(g => g.GetViewAsync(_org, null, 0, 100, CancellationToken.None));
        Assert.Equal(["component:a", "component:b", "component:shared"], view.Nodes.Select(n => n.Ref.ToString()));
        Assert.Equal(["component:b DependsOn component:shared"], view.Edges.Select(e => $"{e.From} {e.Relation} {e.To}"));

        // The edge both sources reported survives with the remaining source as its provenance.
        var shared = await _catalog.WithDbAsync(db => db.Set<SoftwareEdgeRow>().SingleAsync(e => e.OrganizationId == _org && e.Relation == "CONSUMES"));
        Assert.Equal("repo:b#catalog", shared.SourceLocator);

        // When the last source stops reporting an entity it disappears.
        var gone = await _catalog.ImportAsync(_org, Manifest("repo:b#catalog").Entities("component:b"));
        Assert.Equal(new CatalogImportResult(0, 0, 1, 2), gone);
        Assert.Null(await _catalog.QueryAsync(g => g.FindAsync(_org, Ref("component:shared"), CancellationToken.None)));

        // An empty import retracts everything the source said.
        Assert.Equal(new CatalogImportResult(0, 0, 1, 0), await _catalog.ImportAsync(_org, Manifest("repo:a#catalog")));
        Assert.Equal(0, await _catalog.WithDbAsync(db => db.Set<SoftwareEntityObservationRow>().CountAsync(o => o.OrganizationId == _org && o.SourceLocator == "repo:a#catalog")));
    }

    [Fact]
    public async Task A_lower_trust_source_cannot_overwrite_a_higher_trust_one()
    {
        await _catalog.ImportAsync(_org, Backstage()
            .Entity("component:gateway", "Gateway (catalog)", ["go"])
            .Edge("team:platform", RelationType.Owns, "component:gateway"));
        var versionAfterCatalog = await VersionAsync();

        // A repository manifest and a code scan both restate the same facts differently.
        var manifest = await _catalog.ImportAsync(_org, Manifest()
            .Entity("component:gateway", "Gateway (manifest)", ["rust"])
            .Edge("team:platform", RelationType.Owns, "component:gateway"));
        var scan = await _catalog.ImportAsync(_org, Inferred()
            .Entity("component:gateway", "gateway (guessed)")
            .Edge("team:platform", RelationType.Owns, "component:gateway", confidence: 0.4));

        Assert.Equal(new CatalogImportResult(0, 0, 0, 0), manifest);
        Assert.Equal(new CatalogImportResult(0, 0, 0, 0), scan);
        Assert.Equal(versionAfterCatalog, await VersionAsync());

        var entity = (await _catalog.QueryAsync(g => g.FindAsync(_org, Ref("component:gateway"), CancellationToken.None)))!;
        Assert.Equal("Gateway (catalog)", entity.Title);
        Assert.Equal(["go"], entity.Technologies.AsEnumerable());
        Assert.Equal(ProvenanceSource.Catalog, entity.Provenance.Source);
        var edge = await _catalog.WithDbAsync(db => db.Set<SoftwareEdgeRow>().SingleAsync(e => e.OrganizationId == _org));
        Assert.Equal("catalog", edge.SourceType);
        Assert.Equal(1.0, edge.Confidence);
        Assert.True(edge.IsFact);

        // When the catalog withdraws the entity, the next most trusted observation takes over.
        var withdrawn = await _catalog.ImportAsync(_org, Backstage());
        Assert.Equal(new CatalogImportResult(1, 1, 0, 0), withdrawn);
        entity = (await _catalog.QueryAsync(g => g.FindAsync(_org, Ref("component:gateway"), CancellationToken.None)))!;
        Assert.Equal("Gateway (manifest)", entity.Title);
        Assert.Equal(ProvenanceSource.RepositoryManifest, entity.Provenance.Source);

        // And a higher-trust source arriving later does take precedence.
        await _catalog.ImportAsync(_org, Backstage().Entity("component:gateway", "Gateway (catalog again)"));
        Assert.Equal("Gateway (catalog again)", (await _catalog.QueryAsync(g => g.FindAsync(_org, Ref("component:gateway"), CancellationToken.None)))!.Title);
    }

    [Fact]
    public async Task Confirming_an_inferred_edge_makes_it_a_fact_that_survives_reimport()
    {
        await _catalog.ImportAsync(_org, Inferred().Edge("component:web", RelationType.DependsOn, "component:auth", confidence: 0.6));
        var before = await VersionAsync();

        Assert.False(await _catalog.ConfirmAsync(_org, "component:web", RelationType.DependsOn, "component:missing"));
        Assert.True(await _catalog.ConfirmAsync(_org, "component:web", RelationType.DependsOn, "component:auth", "alice"));
        Assert.NotEqual(before, await VersionAsync());
        var confirmedVersion = await VersionAsync();

        // Confirming again, or the scanner reporting the edge again as unconfirmed, changes nothing.
        Assert.True(await _catalog.ConfirmAsync(_org, "component:web", RelationType.DependsOn, "component:auth", "bob"));
        await _catalog.ImportAsync(_org, Inferred(at: T0.AddHours(1)).Edge("component:web", RelationType.DependsOn, "component:auth", confidence: 0.6));
        Assert.Equal(confirmedVersion, await VersionAsync());

        var edge = await _catalog.WithDbAsync(db => db.Set<SoftwareEdgeRow>().SingleAsync(e => e.OrganizationId == _org));
        Assert.True(edge.Confirmed);
        Assert.True(edge.IsFact);
        Assert.Equal("alice", edge.ConfirmedBy);
        Assert.Equal("code-inference", edge.SourceType);
        Assert.Equal(0.6, edge.Confidence);
    }

    [Fact]
    public async Task Invalid_imports_are_rejected_before_anything_is_written()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _catalog.ImportAsync(_org, Manifest(" ").Entities("component:a")));
        await Assert.ThrowsAsync<ArgumentException>(() => _catalog.ImportAsync(_org, Manifest().Edge("component:a", RelationType.DependsOn, "component:b", confidence: 1.5)));

        Assert.Equal("0", await VersionAsync());
    }
}
