using System.Text.Json;
using Axiom.Application.Common;
using Axiom.Application.Governance;
using Axiom.Domain.Governance;
using Axiom.Infrastructure.Governance.Parsing;
using Axiom.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Axiom.IntegrationTests.Governance;

[Collection(PostgresTests.Name)]
public class GovernanceSynchronizationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task First_sync_publishes_the_source_head_with_provenance_and_events()
    {
        using var repo = new GovernanceRepo();
        await using var harness = new GovernanceHarness(postgres);
        var organization = PostgresFixture.NewOrganization();
        var head = repo.WithSpecExamples()
            .Write("README.md", "# Governance")
            .Write("docs/decisions/ARCH-900.yaml", "this file is outside the governance root and is never read")
            .Commit("Add governance");
        var config = repo.ConfigFor(organization);

        var result = await harness.SyncAsync(config);

        Assert.Equal(SyncOutcome.Published, result.Outcome);
        Assert.Equal(head, result.SourceCommit);
        Assert.Equal(2, result.RecordCount);
        Assert.Equal(["ARCH-042", "EXC-023"], result.ChangedRecordIds.AsEnumerable());
        // ARCH-042 relates to a record of another registry: a warning, which does not block publication (ADR-0008).
        var warning = Assert.Single(result.Issues);
        Assert.Equal(IssueSeverity.Warning, warning.Severity);
        Assert.Equal(IssueCodes.UnknownRelationTarget, warning.Code);

        var info = await harness.SnapshotsAsync(p => p.GetCurrentInfoAsync(organization, CancellationToken.None));
        Assert.NotNull(info);
        Assert.Equal(result.SnapshotId, info.SnapshotId);
        Assert.Equal(head, info.SourceCommit);
        Assert.Equal(2, info.RecordCount);
        Assert.Equal(harness.Time.GetUtcNow(), info.PublishedAt);

        var snapshot = await harness.SnapshotsAsync(p => p.GetCurrentAsync(organization, CancellationToken.None));
        Assert.NotNull(snapshot);
        Assert.Equal(result.SnapshotId, snapshot.Id);
        var decision = snapshot.Find("ARCH-042")!;
        Assert.Equal(1, decision.Revision);
        Assert.Equal(new SourceProvenance(repo.Location, head, Records.GatewayDecisionPath, decision.Provenance.BlobSha), decision.Provenance);
        Assert.Equal(40, decision.Provenance.BlobSha.Length);
        Assert.StartsWith("## Context", decision.Record.Body, StringComparison.Ordinal);
        // The snapshot is exactly what the parser makes of the file in Git.
        Assert.Equal(new GovernanceRecordParser().Parse(Records.GatewayDecisionPath, Records.SpecExample("decisions/ARCH-042-gateway-routing.md")).Record!.ContentHash, decision.Record.ContentHash);

        var changed = await harness.EventsAsync(organization, EventTypes.GovernanceRecordChanged);
        Assert.Equal(2, changed.Count);
        var data = changed.Select(e => JsonDocument.Parse(e.Data).RootElement).Single(d => d.GetProperty("recordId").GetString() == "ARCH-042");
        Assert.Equal("decision", data.GetProperty("kind").GetString());
        Assert.Equal("added", data.GetProperty("changeType").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("oldLifecycle").ValueKind);
        Assert.Equal("accepted", data.GetProperty("newLifecycle").GetString());
        Assert.Equal(repo.Location, data.GetProperty("repository").GetString());
        Assert.Equal(head, data.GetProperty("commit").GetString());
        Assert.Equal(Records.GatewayDecisionPath, data.GetProperty("path").GetString());
        Assert.Equal("gui-platform", data.GetProperty("scope").GetProperty("system")[0].GetString());

        var published = Assert.Single(await harness.EventsAsync(organization, EventTypes.GovernanceSnapshotPublished));
        Assert.Equal(IntegrationEvent.SchemaVersion, published.SchemaVersion);
        var publishedData = JsonDocument.Parse(published.Data).RootElement;
        Assert.Equal(result.SnapshotId, publishedData.GetProperty("snapshotId").GetString());
        Assert.Equal(head, publishedData.GetProperty("sourceCommit").GetString());
        Assert.Equal(2, publishedData.GetProperty("recordCounts").GetProperty("total").GetInt32());
        Assert.Equal(2, publishedData.GetProperty("recordCounts").GetProperty("added").GetInt32());
        Assert.Equal("valid_with_warnings", publishedData.GetProperty("validationStatus").GetString());

        var again = await harness.SyncAsync(config);
        Assert.Equal(SyncOutcome.UpToDate, again.Outcome);
        Assert.Equal(result.SnapshotId, again.SnapshotId);
        Assert.Empty(again.ChangedRecordIds);
    }

    [Fact]
    public async Task Incremental_sync_applies_additions_modifications_deletions_and_renames()
    {
        using var repo = new GovernanceRepo();
        await using var harness = new GovernanceHarness(postgres);
        var organization = PostgresFixture.NewOrganization();
        var config = repo.ConfigFor(organization);
        repo.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", "Health endpoints"))
            .Write(Records.PathOf("ARCH-002"), Records.Decision("ARCH-002", "Structured logging", status: "proposed"))
            .Write(Records.PathOf("ARCH-003"), Records.Decision("ARCH-003", "Retire the batch host"))
            .Commit("Initial records");
        var first = await harness.SyncAsync(config);
        Assert.Equal(3, first.RecordCount);

        repo.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", "Health and readiness endpoints")).Commit("Modify ARCH-001");
        repo.Delete(Records.PathOf("ARCH-003")).Commit("Delete ARCH-003");
        var renamed = repo.Move(Records.PathOf("ARCH-002"), "governance/decisions/logging/structured.yaml").Commit("Rename ARCH-002");
        var head = repo.Write(Records.PathOf("ARCH-004"), Records.Decision("ARCH-004", "Trace context propagation")).Commit("Add ARCH-004");

        var result = await harness.SyncAsync(config);

        Assert.Equal(SyncOutcome.Published, result.Outcome);
        Assert.Equal(head, result.SourceCommit);
        Assert.Equal(3, result.RecordCount);
        Assert.Equal(["ARCH-001", "ARCH-002", "ARCH-003", "ARCH-004"], result.ChangedRecordIds.AsEnumerable());
        Assert.NotEqual(first.SnapshotId, result.SnapshotId);
        // One snapshot row per published commit, in order.
        Assert.Equal([1, 2, 3, 4, 5], (await harness.SnapshotRowsAsync(organization)).Select(s => s.Sequence));

        var modified = (await harness.GetAsync(organization, "ARCH-001"))!;
        Assert.Equal(2, modified.Current.Revision);
        Assert.Equal("Health and readiness endpoints", modified.Current.Record.Title);
        Assert.Equal([1, 2], modified.History.Select(h => h.Revision));
        Assert.NotEqual(modified.History[0].ContentHash, modified.History[1].ContentHash);
        Assert.Equal("Ada Lovelace <ada@example.test>", modified.History[1].Author);

        // A rename keeps the content, so it keeps the revision; only the coordinates move.
        var moved = (await harness.GetAsync(organization, "ARCH-002"))!;
        Assert.Equal(1, moved.Current.Revision);
        Assert.Equal("governance/decisions/logging/structured.yaml", moved.Current.Provenance.Path);
        Assert.Equal(renamed, moved.Current.Provenance.CommitSha);
        Assert.Single(moved.History);

        // A removed record leaves the snapshot and search, but its history stays for audit.
        Assert.Null(await harness.GetAsync(organization, "ARCH-003"));
        var current = (await harness.SnapshotsAsync(p => p.GetCurrentAsync(organization, CancellationToken.None)))!;
        Assert.Null(current.Find("ARCH-003"));
        Assert.Equal(["ARCH-001", "ARCH-002", "ARCH-004"], current.Revisions.Select(r => r.Id));
        var removedRevision = await harness.InScopeAsync(s => s.GetRequiredService<IGovernanceQueries>().GetRevisionAsync(organization, "ARCH-003", 1, CancellationToken.None));
        Assert.Equal("Retire the batch host", removedRevision!.Record.Title);
        var before = (await harness.SnapshotsAsync(p => p.GetAsync(organization, first.SnapshotId!, CancellationToken.None)))!;
        Assert.NotNull(before.Find("ARCH-003"));

        var changeTypes = (await harness.EventsAsync(organization, EventTypes.GovernanceRecordChanged))
            .Select(e => JsonDocument.Parse(e.Data).RootElement)
            .Select(d => $"{d.GetProperty("recordId").GetString()}:{d.GetProperty("changeType").GetString()}")
            .Order(StringComparer.Ordinal);
        Assert.Equal(
            ["ARCH-001:added", "ARCH-001:modified", "ARCH-002:added", "ARCH-002:moved", "ARCH-003:added", "ARCH-003:removed", "ARCH-004:added"],
            changeTypes);
    }

    [Fact]
    public async Task Content_seen_before_returns_to_its_original_revision_number()
    {
        using var repo = new GovernanceRepo();
        await using var harness = new GovernanceHarness(postgres);
        var organization = PostgresFixture.NewOrganization();
        var config = repo.ConfigFor(organization);
        var original = Records.Decision("ARCH-001", "Health endpoints");
        repo.Write(Records.PathOf("ARCH-001"), original).Commit("v1");
        var first = await harness.SyncAsync(config);
        repo.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", "Health and readiness endpoints")).Commit("v2");
        repo.Write(Records.PathOf("ARCH-001"), original).Commit("revert to v1");

        var result = await harness.SyncAsync(config);

        var detail = (await harness.GetAsync(organization, "ARCH-001"))!;
        Assert.Equal(1, detail.Current.Revision);
        Assert.Equal([1, 2], detail.History.Select(h => h.Revision));
        // Same content, same content-addressed snapshot.
        Assert.Equal(first.SnapshotId, result.SnapshotId);
        Assert.Equal(3, (await harness.SnapshotRowsAsync(organization)).Count);
    }

    [Fact]
    public async Task Invalid_commit_is_rejected_and_the_previous_snapshot_stays_current_until_a_fix_publishes()
    {
        using var repo = new GovernanceRepo();
        await using var harness = new GovernanceHarness(postgres);
        var organization = PostgresFixture.NewOrganization();
        var config = repo.ConfigFor(organization);
        var good = repo.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", "Health endpoints"))
            .Write(Records.PathOf("ARCH-002"), Records.Decision("ARCH-002", "Structured logging"))
            .Commit("Initial records");
        var published = await harness.SyncAsync(config);

        // The edit breaks ARCH-002 (no owner) and, in the same commit, adds a perfectly valid ARCH-003.
        var broken = repo.Write(Records.PathOf("ARCH-002"), Records.Decision("ARCH-002", "Structured logging", owner: ""))
            .Write(Records.PathOf("ARCH-003"), Records.Decision("ARCH-003", "Trace context propagation"))
            .Commit("Break ARCH-002");
        var rejected = await harness.SyncAsync(config);

        Assert.Equal(SyncOutcome.Rejected, rejected.Outcome);
        Assert.Equal(broken, rejected.SourceCommit);
        Assert.Equal(published.SnapshotId, rejected.SnapshotId);
        Assert.Empty(rejected.ChangedRecordIds);
        Assert.Contains(rejected.Issues, i => i.Severity == IssueSeverity.Error && i.SourcePath == Records.PathOf("ARCH-002"));

        // ADR-0008: nothing of the rejected commit is visible, not even its valid record.
        var info = (await harness.SnapshotsAsync(p => p.GetCurrentInfoAsync(organization, CancellationToken.None)))!;
        Assert.Equal(published.SnapshotId, info.SnapshotId);
        Assert.Equal(good, info.SourceCommit);
        Assert.Null(await harness.GetAsync(organization, "ARCH-003"));
        Assert.Equal(["platform-architecture"], (await harness.GetAsync(organization, "ARCH-002"))!.Current.Record.Owners.AsEnumerable());
        Assert.Single(await harness.SnapshotRowsAsync(organization));
        Assert.Single(await harness.EventsAsync(organization, EventTypes.GovernanceSnapshotPublished));

        // The issues are kept on the checkpoint, so asking again reports the same rejection.
        var asked = await harness.SyncAsync(config);
        Assert.Equal(SyncOutcome.Rejected, asked.Outcome);
        Assert.Equal(rejected.Issues.Select(i => i.Code), asked.Issues.Select(i => i.Code));

        var fix = repo.Write(Records.PathOf("ARCH-002"), Records.Decision("ARCH-002", "Structured logging", owner: "team-observability")).Commit("Fix ARCH-002");
        var fixedResult = await harness.SyncAsync(config);

        Assert.Equal(SyncOutcome.Published, fixedResult.Outcome);
        Assert.Equal(3, fixedResult.RecordCount);
        Assert.Equal(["ARCH-002", "ARCH-003"], fixedResult.ChangedRecordIds.AsEnumerable());
        Assert.Empty(fixedResult.Issues);
        var logging = (await harness.GetAsync(organization, "ARCH-002"))!;
        // The rejected content never became a revision.
        Assert.Equal(2, logging.Current.Revision);
        Assert.Equal([good, fix], logging.History.Select(h => h.CommitSha));
        Assert.Equal([good, fix], (await harness.SnapshotRowsAsync(organization)).Select(s => s.SourceCommit));
    }

    [Fact]
    public async Task Unparseable_record_and_broken_relationship_reject_the_commit()
    {
        using var repo = new GovernanceRepo();
        await using var harness = new GovernanceHarness(postgres);
        var organization = PostgresFixture.NewOrganization();
        var config = repo.ConfigFor(organization);
        repo.Write(Records.PathOf("ARCH-001"), "kind: [unterminated").Commit("Garbage");

        var garbage = await harness.SyncAsync(config);

        Assert.Equal(SyncOutcome.Rejected, garbage.Outcome);
        Assert.Null(garbage.SnapshotId);
        Assert.Null(await harness.SnapshotsAsync(p => p.GetCurrentAsync(organization, CancellationToken.None)));

        repo.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", "Health endpoints", relationships: "{refines: [ARCH-777]}")).Commit("Dangling refinement");
        var dangling = await harness.SyncAsync(config);

        Assert.Equal(SyncOutcome.Rejected, dangling.Outcome);
        Assert.Contains(dangling.Issues, i => i.Code == IssueCodes.UnknownRelationTarget && i.Severity == IssueSeverity.Error);

        repo.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", "Health endpoints")).Commit("Valid");
        var valid = await harness.SyncAsync(config);

        Assert.Equal(SyncOutcome.Published, valid.Outcome);
        Assert.Equal(1, Assert.Single(await harness.SnapshotRowsAsync(organization)).Sequence);
    }

    [Fact]
    public async Task Illegal_lifecycle_transition_is_rejected_against_the_last_published_revision()
    {
        using var repo = new GovernanceRepo();
        await using var harness = new GovernanceHarness(postgres);
        var organization = PostgresFixture.NewOrganization();
        var config = repo.ConfigFor(organization);
        repo.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", "Health endpoints", status: "accepted")).Commit("Accepted");
        var accepted = await harness.SyncAsync(config);

        repo.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", "Health endpoints", status: "proposed")).Commit("Back to proposed");
        var illegal = await harness.SyncAsync(config);

        Assert.Equal(SyncOutcome.Rejected, illegal.Outcome);
        var issue = Assert.Single(illegal.Issues);
        Assert.Equal(IssueCodes.IllegalTransition, issue.Code);
        Assert.Equal("ARCH-001", issue.RecordId);
        Assert.Equal(accepted.SnapshotId, illegal.SnapshotId);
        Assert.Equal(LifecycleStatus.Accepted, (await harness.GetAsync(organization, "ARCH-001"))!.Current.Record.Status);

        // A second illegal attempt is still judged against the published 'accepted', not the rejected 'proposed'.
        repo.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", "Health endpoints", status: "rejected")).Commit("Rejected");
        Assert.Equal(SyncOutcome.Rejected, (await harness.SyncAsync(config)).Outcome);

        repo.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", "Health endpoints", status: "deprecated")).Commit("Deprecated");
        var legal = await harness.SyncAsync(config);

        Assert.Equal(SyncOutcome.Published, legal.Outcome);
        var detail = (await harness.GetAsync(organization, "ARCH-001"))!;
        Assert.Equal(LifecycleStatus.Deprecated, detail.Current.Record.Status);
        Assert.Equal([LifecycleStatus.Accepted, LifecycleStatus.Deprecated], detail.History.Select(h => h.Status));
        var change = (await harness.EventsAsync(organization, EventTypes.GovernanceRecordChanged))
            .Select(e => JsonDocument.Parse(e.Data).RootElement)
            .Single(d => d.GetProperty("changeType").GetString() == "modified");
        Assert.Equal("accepted", change.GetProperty("oldLifecycle").GetString());
        Assert.Equal("deprecated", change.GetProperty("newLifecycle").GetString());
    }

    [Fact]
    public async Task Events_are_not_duplicated_when_sync_is_run_again_or_the_projection_is_rebuilt()
    {
        using var repo = new GovernanceRepo();
        await using var harness = new GovernanceHarness(postgres);
        var organization = PostgresFixture.NewOrganization();
        var config = repo.ConfigFor(organization);
        repo.WithSpecExamples().Commit("Add governance");
        repo.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", "Health endpoints")).Commit("Add ARCH-001");
        await harness.SyncAsync(config);
        var events = await harness.EventsAsync(organization);
        Assert.Equal(5, events.Count);

        Assert.Equal(SyncOutcome.UpToDate, (await harness.SyncAsync(config)).Outcome);
        Assert.Equal(SyncOutcome.Published, (await harness.SyncAsync(config, SyncMode.Rebuild)).Outcome);

        var after = await harness.EventsAsync(organization);
        Assert.Equal(events.Select(e => e.EventId), after.Select(e => e.EventId));
        Assert.Equal(events.Select(e => e.Data), after.Select(e => e.Data));
    }

    [Fact]
    public async Task Rewritten_history_falls_back_to_a_rebuild()
    {
        using var repo = new GovernanceRepo();
        await using var harness = new GovernanceHarness(postgres);
        var organization = PostgresFixture.NewOrganization();
        var config = repo.ConfigFor(organization);
        var root = repo.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", "Health endpoints")).Commit("Add ARCH-001");
        var abandoned = repo.Write(Records.PathOf("ARCH-002"), Records.Decision("ARCH-002", "Structured logging")).Commit("Add ARCH-002");
        await harness.SyncAsync(config);

        repo.ResetHard(root);
        var rewritten = repo.Write(Records.PathOf("ARCH-003"), Records.Decision("ARCH-003", "Trace context propagation")).Commit("Add ARCH-003 instead");

        var source = harness.Services.GetRequiredService<IGovernanceSource>();
        await source.FetchHeadAsync(config, CancellationToken.None);
        Assert.Null(await source.ChangedPathsAsync(config, abandoned, rewritten, CancellationToken.None));

        var result = await harness.SyncAsync(config);

        Assert.Equal(SyncOutcome.Published, result.Outcome);
        Assert.Equal(rewritten, result.SourceCommit);
        Assert.Equal(["ARCH-001", "ARCH-003"], (await harness.SearchAsync(organization, new GovernanceQuery())).Items.Select(i => i.Id));
        // The projection holds only what the new history contains: the abandoned commit left no trace.
        Assert.Equal([root, rewritten], (await harness.SnapshotRowsAsync(organization)).Select(s => s.SourceCommit));
        Assert.Null(await harness.InScopeAsync(s => s.GetRequiredService<IGovernanceQueries>().GetRevisionAsync(organization, "ARCH-002", 1, CancellationToken.None)));

        // It is what a rebuild of the rewritten history produces.
        var incremental = await harness.DumpProjectionAsync(organization);
        await harness.SyncAsync(config, SyncMode.Rebuild);
        Assert.Equal(incremental, await harness.DumpProjectionAsync(organization));
    }

    [Fact]
    public async Task Changing_the_configured_source_rebuilds_from_the_new_source()
    {
        using var first = new GovernanceRepo();
        using var second = new GovernanceRepo();
        await using var harness = new GovernanceHarness(postgres);
        var organization = PostgresFixture.NewOrganization();
        first.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", "Health endpoints")).Commit("First source");
        second.Write(Records.PathOf("ARCH-002"), Records.Decision("ARCH-002", "Structured logging")).Commit("Second source");
        await harness.SyncAsync(first.ConfigFor(organization));

        var result = await harness.SyncAsync(second.ConfigFor(organization));

        Assert.Equal(SyncOutcome.Published, result.Outcome);
        var only = Assert.Single((await harness.SearchAsync(organization, new GovernanceQuery())).Items);
        Assert.Equal("ARCH-002", only.Id);
        Assert.Equal(second.Location, (await harness.GetAsync(organization, "ARCH-002"))!.SourceRepository);
    }

    [Fact]
    public async Task Concurrent_synchronizers_for_one_organization_do_not_corrupt_the_projection()
    {
        using var repo = new GovernanceRepo();
        await using var harness = new GovernanceHarness(postgres);
        var organization = PostgresFixture.NewOrganization();
        var config = repo.ConfigFor(organization);
        var commits = new List<string>();
        for (var i = 1; i <= 6; i++)
        {
            commits.Add(repo.Write(Records.PathOf($"ARCH-00{i}"), Records.Decision($"ARCH-00{i}", $"Decision number {i}")).Commit($"Add ARCH-00{i}"));
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => harness.SyncAsync(config))));

        // Exactly one run did the work; the others waited for it and found nothing left to do.
        Assert.Single(results, r => r.Outcome == SyncOutcome.Published);
        Assert.Equal(3, results.Count(r => r.Outcome == SyncOutcome.UpToDate));
        Assert.All(results, r => Assert.Equal(results[0].SnapshotId, r.SnapshotId));
        Assert.Equal(commits, (await harness.SnapshotRowsAsync(organization)).Select(s => s.SourceCommit));
        Assert.Equal(6, (await harness.SearchAsync(organization, new GovernanceQuery())).Total);
        Assert.Equal(12, (await harness.EventsAsync(organization)).Count);
    }

    [Fact]
    public async Task Organizations_with_the_same_record_ids_never_see_each_other()
    {
        using var repoA = new GovernanceRepo();
        using var repoB = new GovernanceRepo();
        await using var harness = new GovernanceHarness(postgres);
        var organizationA = PostgresFixture.NewOrganization();
        var organizationB = PostgresFixture.NewOrganization();
        repoA.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", "Alpha health endpoints", owner: "alpha-team", statement: "Alpha services expose health endpoints."))
            .Write(Records.PathOf("ARCH-002"), Records.Decision("ARCH-002", "Alpha only decision", relationships: "{refines: [ARCH-001]}"))
            .Commit("Alpha governance");
        repoB.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", "Bravo health endpoints", owner: "bravo-team", statement: "Bravo services expose health endpoints."))
            .Commit("Bravo governance");

        var resultA = await harness.SyncAsync(repoA.ConfigFor(organizationA));
        var resultB = await harness.SyncAsync(repoB.ConfigFor(organizationB));

        Assert.NotEqual(resultA.SnapshotId, resultB.SnapshotId);
        var searchB = await harness.SearchAsync(organizationB, new GovernanceQuery());
        Assert.Equal("Bravo health endpoints", Assert.Single(searchB.Items).Title);
        Assert.Equal(2, (await harness.SearchAsync(organizationA, new GovernanceQuery())).Total);

        // Filters cannot reach across: text, owner, ID and relation of the other organization find nothing.
        Assert.Equal(0, (await harness.SearchAsync(organizationB, new GovernanceQuery { Text = "Alpha" })).Total);
        Assert.Equal(0, (await harness.SearchAsync(organizationB, new GovernanceQuery { Owner = "alpha-team" })).Total);
        Assert.Equal(0, (await harness.SearchAsync(organizationB, new GovernanceQuery { Ids = ["ARCH-002"] })).Total);
        Assert.Equal(0, (await harness.SearchAsync(organizationB, new GovernanceQuery { RelatedTo = "ARCH-001" })).Total);
        Assert.Null(await harness.GetAsync(organizationB, "ARCH-002"));
        Assert.Empty((await harness.GetAsync(organizationB, "ARCH-001"))!.IncomingRelations);
        Assert.Equal("Alpha health endpoints", (await harness.GetAsync(organizationA, "ARCH-001"))!.Current.Record.Title);
        Assert.Equal(repoB.Location, (await harness.GetAsync(organizationB, "ARCH-001"))!.SourceRepository);

        // A snapshot ID of one organization does not resolve for another.
        Assert.Null(await harness.SnapshotsAsync(p => p.GetAsync(organizationB, resultA.SnapshotId!, CancellationToken.None)));
        Assert.Equal("Bravo health endpoints", (await harness.SnapshotsAsync(p => p.GetCurrentAsync(organizationB, CancellationToken.None)))!.Find("ARCH-001")!.Record.Title);

        // Rebuilding one organization leaves the other's projection and events alone.
        var dumpB = await harness.DumpProjectionAsync(organizationB);
        var eventsB = (await harness.EventsAsync(organizationB)).Select(e => e.EventId).ToList();
        await harness.SyncAsync(repoA.ConfigFor(organizationA), SyncMode.Rebuild);
        Assert.Equal(dumpB, await harness.DumpProjectionAsync(organizationB));
        Assert.Equal(eventsB, (await harness.EventsAsync(organizationB)).Select(e => e.EventId));
        Assert.All(await harness.EventsAsync(organizationA), e => Assert.DoesNotContain("Bravo", e.Data, StringComparison.Ordinal));
    }
}
