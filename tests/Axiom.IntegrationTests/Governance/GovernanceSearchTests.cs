using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Application.Common;
using Axiom.Application.Governance;
using Axiom.Domain.Governance;
using Axiom.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Axiom.IntegrationTests.Governance;

/// <summary>Search (R19), lifecycle visibility (R2) and staleness (R20, R21) over a synchronized projection.</summary>
[Collection(PostgresTests.Name)]
public class GovernanceSearchTests(PostgresFixture postgres)
{
    private static readonly string[] All = ["ARCH-001", "ARCH-002", "ARCH-003", "ARCH-004", "ARCH-005", "ARCH-042", "EXC-023"];

    /// <summary>
    /// The specification's examples (ARCH-042, EXC-023) plus one record per lifecycle state.
    /// "Today" is 2026-10-04: ARCH-002 and ARCH-005 are past their review date, ARCH-042 is not.
    /// </summary>
    private static async Task<(GovernanceSourceConfig Config, string Head)> SeedAsync(GovernanceHarness harness, GovernanceRepo repo)
    {
        var config = repo.ConfigFor(PostgresFixture.NewOrganization());
        var head = repo.WithSpecExamples()
            .Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", "MySQL everywhere", status: "superseded", owner: "team-data", tags: "storage",
                scope: "{technologies: [MySQL]}", statement: "Every service stores relational data in MySQL."))
            .Write(Records.PathOf("ARCH-002"), Records.Decision("ARCH-002", "Default relational database", owner: "team-data", tags: "storage, relational-default",
                scope: "{technologies: [PostgreSQL], environments: [production]}", statement: "PostgreSQL is the default relational database.",
                relationships: "{supersedes: [ARCH-001]}", reviewAfter: "2026-01-01"))
            .Write(Records.PathOf("ARCH-003"), Records.Decision("ARCH-003", "GraphQL federation", status: "rejected", owner: "team-web",
                statement: "Adopt GraphQL federation for all public interfaces."))
            .Write(Records.PathOf("ARCH-004"), Records.Decision("ARCH-004", "Server rendered pages", status: "deprecated", owner: "team-web", tags: "frontend",
                statement: "Pages are rendered on the server."))
            .Write(Records.PathOf("ARCH-005"), Records.Decision("ARCH-005", "Edge caching", status: "proposed", owner: "team-web", tags: "frontend",
                statement: "Static assets are cached at the edge.", reviewAfter: "2026-01-01"))
            .Commit("Seed governance");
        var result = await harness.SyncAsync(config);
        Assert.Equal(SyncOutcome.Published, result.Outcome);
        return (config, head);
    }

    private static IEnumerable<string> Ids(GovernanceSearchResult result) => result.Items.Select(i => i.Id);

    [Fact]
    public async Task Search_supports_every_filter_with_paging_and_totals()
    {
        using var repo = new GovernanceRepo();
        await using var harness = new GovernanceHarness(postgres);
        var (config, head) = await SeedAsync(harness, repo);
        var organization = config.OrganizationId;
        Task<GovernanceSearchResult> Search(GovernanceQuery query) => harness.SearchAsync(organization, query);

        // No filter: everything, ordered by ID, whatever the lifecycle state (R2).
        var everything = await Search(new GovernanceQuery());
        Assert.Equal(7, everything.Total);
        Assert.Equal(All, Ids(everything));

        // Full text over statement, title, tags and ID.
        Assert.Equal(["ARCH-002"], Ids(await Search(new GovernanceQuery { Text = "PostgreSQL" })));
        Assert.Equal(["ARCH-003"], Ids(await Search(new GovernanceQuery { Text = "federation" })));
        Assert.Equal(["ARCH-002"], Ids(await Search(new GovernanceQuery { Text = "relational-default" })));
        Assert.Equal(["ARCH-001", "ARCH-002"], Ids(await Search(new GovernanceQuery { Text = "storage" })));
        Assert.Equal(["ARCH-004"], Ids(await Search(new GovernanceQuery { Text = "ARCH-004" })));
        Assert.Equal(["ARCH-042"], Ids(await Search(new GovernanceQuery { Text = "gateway business routing logic" })));
        // Stemming: "rendering" finds "rendered"; all terms must match.
        Assert.Equal(["ARCH-004"], Ids(await Search(new GovernanceQuery { Text = "rendering pages" })));
        Assert.Equal(0, (await Search(new GovernanceQuery { Text = "PostgreSQL federation" })).Total);
        Assert.Equal(0, (await Search(new GovernanceQuery { Text = "kubernetes" })).Total);

        // IDs, kinds, statuses.
        Assert.Equal(["ARCH-001", "ARCH-003"], Ids(await Search(new GovernanceQuery { Ids = ["ARCH-003", "ARCH-001", "ARCH-999"] })));
        Assert.Equal(["EXC-023"], Ids(await Search(new GovernanceQuery { Kinds = [RecordKind.Exception] })));
        Assert.Equal(6, (await Search(new GovernanceQuery { Kinds = [RecordKind.Decision, RecordKind.Standard] })).Total);
        Assert.Equal(["ARCH-005"], Ids(await Search(new GovernanceQuery { Statuses = [LifecycleStatus.Proposed] })));

        // R2: superseded, deprecated and rejected records stay searchable, and none of them governs.
        var historical = await Search(new GovernanceQuery { Statuses = [LifecycleStatus.Superseded, LifecycleStatus.Deprecated, LifecycleStatus.Rejected] });
        Assert.Equal(["ARCH-001", "ARCH-003", "ARCH-004"], Ids(historical));
        Assert.All(historical.Items, item => Assert.False(item.IsAuthoritative));

        // Owner and tag.
        Assert.Equal(["ARCH-003", "ARCH-004", "ARCH-005"], Ids(await Search(new GovernanceQuery { Owner = "team-web" })));
        Assert.Equal(["ARCH-042"], Ids(await Search(new GovernanceQuery { Owner = "platform-architecture" })));
        Assert.Equal(["ARCH-004", "ARCH-005"], Ids(await Search(new GovernanceQuery { Tag = "frontend" })));
        Assert.Equal(0, (await Search(new GovernanceQuery { Tag = "front" })).Total);

        // Scope: technology and system, case-insensitive as in the domain; several dimensions must all match.
        Assert.Equal(["ARCH-002"], Ids(await Search(new GovernanceQuery { Scope = Scope((ScopeDimension.Technology, "postgresql")) })));
        Assert.Equal(["ARCH-042"], Ids(await Search(new GovernanceQuery { Scope = Scope((ScopeDimension.System, "gui-platform")) })));
        Assert.Equal(["ARCH-042", "EXC-023"], Ids(await Search(new GovernanceQuery { Scope = Scope((ScopeDimension.Repository, "gateway")) })));
        Assert.Equal(["ARCH-042"], Ids(await Search(new GovernanceQuery { Scope = Scope((ScopeDimension.Repository, "gateway"), (ScopeDimension.Capability, "routing")) })));
        Assert.Equal(["EXC-023"], Ids(await Search(new GovernanceQuery { Scope = Scope((ScopeDimension.Path, "src/Legacy/Homepage/**")) })));
        Assert.Equal(0, (await Search(new GovernanceQuery { Scope = Scope((ScopeDimension.Technology, "postgresql"), (ScopeDimension.Environment, "staging")) })).Total);

        // Relationships: by kind, by target, and both.
        Assert.Equal(["ARCH-002"], Ids(await Search(new GovernanceQuery { RelationKind = RelationKind.Supersedes })));
        Assert.Equal(["ARCH-002"], Ids(await Search(new GovernanceQuery { RelatedTo = "ARCH-001" })));
        Assert.Equal(["EXC-023"], Ids(await Search(new GovernanceQuery { RelationKind = RelationKind.ExceptionTo, RelatedTo = "ARCH-042" })));
        Assert.Equal(0, (await Search(new GovernanceQuery { RelationKind = RelationKind.Refines, RelatedTo = "ARCH-042" })).Total);

        // Stale: accepted and past review. The overdue proposal is not governing, so it is not stale.
        Assert.Equal(["ARCH-002"], Ids(await Search(new GovernanceQuery { StaleOnly = true })));

        // Filters combine.
        Assert.Equal(["ARCH-002"], Ids(await Search(new GovernanceQuery { Owner = "team-data", Statuses = [LifecycleStatus.Accepted], Text = "database" })));
        Assert.Equal(0, (await Search(new GovernanceQuery { Owner = "team-data", Tag = "frontend" })).Total);

        // Paging keeps the total and the order.
        var firstPage = await Search(new GovernanceQuery { Take = 3 });
        Assert.Equal(7, firstPage.Total);
        Assert.Equal(All[..3], Ids(firstPage));
        Assert.Equal(All[3..6], Ids(await Search(new GovernanceQuery { Skip = 3, Take = 3 })));
        var lastPage = await Search(new GovernanceQuery { Skip = 6, Take = 3 });
        Assert.Equal(7, lastPage.Total);
        Assert.Equal(All[6..], Ids(lastPage));
        Assert.Empty((await Search(new GovernanceQuery { Skip = 7 })).Items);
        Assert.Equal(2, (await Search(new GovernanceQuery { Owner = "team-web", Skip = 1, Take = 5 })).Items.Length);

        // Summaries carry the domain's own judgement and the Git coordinates.
        var summary = everything.Items.Single(i => i.Id == "ARCH-042");
        Assert.Equal(new GovernanceSummary(
            "ARCH-042", RecordKind.Decision, "Gateway must not contain business routing logic", LifecycleStatus.Accepted, IsAuthoritative: true,
            ["platform-architecture"], ["gateway", "routing"], AuthorityLevel.System, Exemptable: true, Revision: 1, new DateOnly(2027, 3, 1), IsStale: false,
            Records.GatewayDecisionPath, head) with { Owners = summary.Owners, Tags = summary.Tags }, summary);
        Assert.Equal(["platform-architecture"], summary.Owners.AsEnumerable());
        Assert.Equal(["gateway", "routing"], summary.Tags.AsEnumerable());
        // An exception waives; it never governs.
        Assert.False(everything.Items.Single(i => i.Id == "EXC-023").IsAuthoritative);
        Assert.False(everything.Items.Single(i => i.Id == "ARCH-005").IsAuthoritative);
    }

    [Fact]
    public async Task Authority_follows_the_validity_window_on_the_injected_clock()
    {
        using var repo = new GovernanceRepo();
        // ARCH-042 becomes effective on 2026-09-01; the clock starts an hour before it.
        await using var harness = new GovernanceHarness(postgres, startAt: new DateTimeOffset(2026, 8, 31, 23, 0, 0, TimeSpan.Zero));
        var (config, _) = await SeedAsync(harness, repo);

        var before = await harness.SearchAsync(config.OrganizationId, new GovernanceQuery { Ids = ["ARCH-042"] });
        Assert.False(Assert.Single(before.Items).IsAuthoritative);

        harness.Time.SetUtcNow(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var after = await harness.SearchAsync(config.OrganizationId, new GovernanceQuery { Ids = ["ARCH-042"] });
        Assert.True(Assert.Single(after.Items).IsAuthoritative);
    }

    [Fact]
    public async Task Detail_returns_current_revision_history_and_incoming_relations()
    {
        using var repo = new GovernanceRepo();
        await using var harness = new GovernanceHarness(postgres);
        var (config, seed) = await SeedAsync(harness, repo);
        var organization = config.OrganizationId;
        var deprecating = repo.Write(Records.PathOf("ARCH-002"), Records.Decision("ARCH-002", "Default relational database", status: "deprecated", owner: "team-data",
            tags: "storage, relational-default", scope: "{technologies: [PostgreSQL], environments: [production]}",
            statement: "PostgreSQL is the default relational database.", relationships: "{supersedes: [ARCH-001]}", reviewAfter: "2026-01-01")).Commit("Deprecate ARCH-002");
        await harness.SyncAsync(config);

        var detail = (await harness.GetAsync(organization, "ARCH-002"))!;

        Assert.Equal(2, detail.Current.Revision);
        Assert.Equal(LifecycleStatus.Deprecated, detail.Current.Record.Status);
        Assert.Equal(new SourceProvenance(repo.Location, deprecating, Records.PathOf("ARCH-002"), detail.Current.Provenance.BlobSha), detail.Current.Provenance);
        Assert.Equal(repo.Location, detail.SourceRepository);
        Assert.Equal(2, detail.History.Length);
        Assert.Equal((1, LifecycleStatus.Accepted, seed), (detail.History[0].Revision, detail.History[0].Status, detail.History[0].CommitSha));
        Assert.Equal((2, LifecycleStatus.Deprecated, deprecating), (detail.History[1].Revision, detail.History[1].Status, detail.History[1].CommitSha));
        Assert.Equal(detail.Current.Record.ContentHash, detail.History[1].ContentHash);
        Assert.Equal(Records.PathOf("ARCH-002"), detail.History[1].Path);
        Assert.True(detail.History[0].CommittedAt < detail.History[1].CommittedAt);

        // Incoming relations name the records that point here.
        Assert.Equal([new Relation(RelationKind.Supersedes, "ARCH-002")], (await harness.GetAsync(organization, "ARCH-001"))!.IncomingRelations.AsEnumerable());
        Assert.Equal([new Relation(RelationKind.ExceptionTo, "EXC-023")], (await harness.GetAsync(organization, "ARCH-042"))!.IncomingRelations.AsEnumerable());
        Assert.Empty(detail.IncomingRelations);
        Assert.Null(await harness.GetAsync(organization, "ARCH-999"));

        // Any revision can be read back from its stored source text.
        var queries = (IServiceProvider s) => s.GetRequiredService<IGovernanceQueries>();
        var first = await harness.InScopeAsync(s => queries(s).GetRevisionAsync(organization, "ARCH-002", 1, CancellationToken.None));
        Assert.Equal(LifecycleStatus.Accepted, first!.Record.Status);
        Assert.Equal(seed, first.Provenance.CommitSha);
        Assert.Equal(detail.History[0].ContentHash, first.Record.ContentHash);
        Assert.Null(await harness.InScopeAsync(s => queries(s).GetRevisionAsync(organization, "ARCH-002", 3, CancellationToken.None)));
    }

    [Fact]
    public async Task Stale_records_are_flagged_once_per_review_date_and_keep_governing()
    {
        using var repo = new GovernanceRepo();
        await using var harness = new GovernanceHarness(postgres);
        var (config, _) = await SeedAsync(harness, repo);
        var organization = config.OrganizationId;
        Task<StaleDetectionResult> Detect() => harness.InScopeAsync(s => s.GetRequiredService<StaleRecordDetector>().DetectAsync(organization, CancellationToken.None));

        var first = await Detect();

        Assert.Equal(["ARCH-002"], first.StaleRecordIds.AsEnumerable());
        Assert.Equal(1, first.NewlyFlagged);
        var notice = Assert.Single(await harness.EventsAsync(organization, EventTypes.GovernanceRecordStale));
        var data = JsonDocument.Parse(notice.Data).RootElement;
        Assert.Equal("ARCH-002", data.GetProperty("recordId").GetString());
        Assert.Equal("2026-01-01", data.GetProperty("reviewDue").GetString());
        Assert.Equal("team-data", data.GetProperty("owners")[0].GetString());
        Assert.Equal(harness.Time.GetUtcNow(), notice.OccurredAt);

        // R21: flagged, not dropped. The stale record still governs and is still in the snapshot.
        var stale = Assert.Single((await harness.SearchAsync(organization, new GovernanceQuery { Ids = ["ARCH-002"] })).Items);
        Assert.True(stale.IsStale);
        Assert.True(stale.IsAuthoritative);
        Assert.Equal(LifecycleStatus.Accepted, stale.Status);
        var snapshot = (await harness.SnapshotsAsync(p => p.GetCurrentAsync(organization, CancellationToken.None)))!;
        Assert.True(snapshot.Find("ARCH-002")!.Record.IsAuthoritativeOn(DateOnly.FromDateTime(harness.Time.GetUtcNow().UtcDateTime)));

        // Running again, even concurrently and on later days, does not notify again.
        harness.Time.Advance(TimeSpan.FromDays(3));
        var repeats = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(Detect)));
        Assert.All(repeats, r => Assert.Equal(0, r.NewlyFlagged));
        Assert.All(repeats, r => Assert.Equal(["ARCH-002"], r.StaleRecordIds.AsEnumerable()));
        Assert.Single(await harness.EventsAsync(organization, EventTypes.GovernanceRecordStale));

        // The day after ARCH-042's review date it becomes stale too; only the new one is notified.
        harness.Time.SetUtcNow(new DateTimeOffset(2027, 3, 1, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(0, (await Detect()).NewlyFlagged);
        harness.Time.SetUtcNow(new DateTimeOffset(2027, 3, 2, 0, 0, 0, TimeSpan.Zero));
        var later = await Detect();
        Assert.Equal(["ARCH-002", "ARCH-042"], later.StaleRecordIds.AsEnumerable());
        Assert.Equal(1, later.NewlyFlagged);

        // A new review date that also passes is a new notice for the same record.
        repo.Write(Records.PathOf("ARCH-002"), Records.Decision("ARCH-002", "Default relational database", owner: "team-data", tags: "storage, relational-default",
            scope: "{technologies: [PostgreSQL], environments: [production]}", statement: "PostgreSQL is the default relational database.",
            relationships: "{supersedes: [ARCH-001]}", reviewAfter: "2026-12-01")).Commit("Review ARCH-002");
        await harness.SyncAsync(config);
        Assert.Equal(1, (await Detect()).NewlyFlagged);
        Assert.Equal(3, (await harness.EventsAsync(organization, EventTypes.GovernanceRecordStale)).Count);

        // Another organization has no stale records and gets no notices.
        var other = PostgresFixture.NewOrganization();
        var none = await harness.InScopeAsync(s => s.GetRequiredService<StaleRecordDetector>().DetectAsync(other, CancellationToken.None));
        Assert.Empty(none.StaleRecordIds);
        Assert.Empty(await harness.EventsAsync(other));
    }

    private static ImmutableDictionary<ScopeDimension, string> Scope(params (ScopeDimension Dimension, string Value)[] filters) =>
        filters.ToImmutableDictionary(f => f.Dimension, f => f.Value);
}
