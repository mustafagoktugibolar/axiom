using Axiom.Application.Governance;
using Axiom.Domain.Governance;
using Axiom.IntegrationTests.Support;

namespace Axiom.IntegrationTests.Governance;

/// <summary>Correctness properties P2 (rebuild equivalence) and P10 (historical retrieval) of requirements.md.</summary>
[Collection(PostgresTests.Name)]
public class GovernanceRebuildTests(PostgresFixture postgres)
{
    private const string V1 = "Health endpoints";
    private const string V2 = "Health and readiness endpoints";

    /// <summary>
    /// A history that exercises every kind of step: additions, modifications, a commit outside the
    /// governance root, rejected commits (broken file, illegal transition), a revert to earlier
    /// content, a rename, a removal, a re-addition and a publication with a warning.
    /// </summary>
    private static readonly Action<GovernanceRepo>[] History =
    [
        repo => repo.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", V1)).Write(Records.PathOf("ARCH-002"), Records.Decision("ARCH-002", "Structured logging")),
        repo => repo.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", V2)),
        repo => repo.Write("README.md", "# Not governance"),
        repo => repo.Write(Records.PathOf("ARCH-002"), "kind: [unterminated"),
        repo => repo.Write(Records.PathOf("ARCH-003"), Records.Decision("ARCH-003", "Trace context propagation")),
        repo => repo.Write(Records.PathOf("ARCH-002"), Records.Decision("ARCH-002", "Structured JSON logging")),
        repo => repo.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", V1)),
        repo => repo.Move(Records.PathOf("ARCH-003"), "governance/decisions/observability/tracing.yaml"),
        repo => repo.Delete(Records.PathOf("ARCH-002")),
        repo => repo.Write("governance/decisions/logging/ARCH-002.yaml", Records.Decision("ARCH-002", "Structured logging")),
        repo => repo.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", V1, status: "proposed")),
        repo => repo.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", V1))
            .Write(Records.PathOf("ARCH-004"), Records.Decision("ARCH-004", "Feature flags", relationships: "{related: [EXT-900]}")),
    ];

    /// <summary>After which commits (1-based) the incremental run synchronizes: single steps, multi-commit steps and rejected heads.</summary>
    private static readonly int[] SyncPoints = [1, 3, 5, 8, 10, 11, 12];

    [Fact]
    public async Task Rebuild_yields_the_same_projection_as_repeated_incremental_syncs()
    {
        using var repo = new GovernanceRepo();
        var organization = PostgresFixture.NewOrganization();
        var config = repo.ConfigFor(organization);
        string incremental;
        SyncResult last;

        await using (var harness = new GovernanceHarness(postgres))
        {
            last = await RunIncrementallyAsync(harness, repo, config);
            incremental = await harness.DumpProjectionAsync(organization);

            // The history did what it was built to do.
            Assert.Equal(SyncOutcome.Published, last.Outcome);
            Assert.Equal(4, last.RecordCount);
            Assert.Equal(IssueSeverity.Warning, Assert.Single(last.Issues).Severity);
            // 12 commits, 3 of them rejected (4, 5 and 11).
            Assert.Equal(9, (await harness.SnapshotRowsAsync(organization)).Count);
            Assert.Equal(1, (await harness.GetAsync(organization, "ARCH-001"))!.Current.Revision);
            var logging = (await harness.GetAsync(organization, "ARCH-002"))!;
            Assert.Equal(1, logging.Current.Revision);
            Assert.Equal([1, 2], logging.History.Select(h => h.Revision));
            Assert.Equal("governance/decisions/logging/ARCH-002.yaml", logging.Current.Provenance.Path);
        }

        // A different process with empty caches and an empty mirror rebuilds from Git alone.
        await using (var harness = new GovernanceHarness(postgres))
        {
            var rebuilt = await harness.SyncAsync(config, SyncMode.Rebuild);

            Assert.Equal(last.SnapshotId, rebuilt.SnapshotId);
            Assert.Equal(last.RecordCount, rebuilt.RecordCount);
            Assert.Equal(incremental, await harness.DumpProjectionAsync(organization));

            // Rebuilding again changes nothing either.
            await harness.SyncAsync(config, SyncMode.Rebuild);
            Assert.Equal(incremental, await harness.DumpProjectionAsync(organization));
        }
    }

    [Fact]
    public async Task Every_published_snapshot_is_retrievable_with_its_exact_revisions_after_a_rebuild()
    {
        using var repo = new GovernanceRepo();
        var organization = PostgresFixture.NewOrganization();
        var config = repo.ConfigFor(organization);
        var observed = new Dictionary<string, string>(StringComparer.Ordinal);
        string afterSecondCommit;

        await using (var harness = new GovernanceHarness(postgres))
        {
            // Synchronize after every commit and remember each snapshot exactly as it was served when it was current.
            foreach (var step in History)
            {
                step(repo);
                repo.Commit("step");
                var result = await harness.SyncAsync(config);
                if (result.Outcome == SyncOutcome.Published)
                {
                    var current = (await harness.SnapshotsAsync(p => p.GetCurrentAsync(organization, CancellationToken.None)))!;
                    Assert.Equal(result.SnapshotId, current.Id);
                    observed.TryAdd(current.Id, Fingerprint(current));
                }
            }

            afterSecondCommit = (await harness.SnapshotRowsAsync(organization))[1].SnapshotId;
            foreach (var (snapshotId, fingerprint) in observed)
            {
                var historical = await harness.SnapshotsAsync(p => p.GetAsync(organization, snapshotId, CancellationToken.None));
                Assert.Equal(fingerprint, Fingerprint(historical!));
            }
        }

        Assert.True(observed.Count >= 7, $"Expected the history to produce several distinct snapshots, got {observed.Count}.");

        await using (var harness = new GovernanceHarness(postgres))
        {
            await harness.SyncAsync(config, SyncMode.Rebuild);

            foreach (var (snapshotId, fingerprint) in observed)
            {
                var historical = await harness.SnapshotsAsync(p => p.GetAsync(organization, snapshotId, CancellationToken.None));
                Assert.NotNull(historical);
                Assert.Equal(snapshotId, historical.Id);
                Assert.Equal(fingerprint, Fingerprint(historical));
            }

            // An old receipt replays against the old revision even though the record has since changed back.
            var old = (await harness.SnapshotsAsync(p => p.GetAsync(organization, afterSecondCommit, CancellationToken.None)))!;
            var then = old.Find("ARCH-001")!;
            Assert.Equal(2, then.Revision);
            Assert.Equal(V2, then.Record.Title);
            Assert.Null(old.Find("ARCH-004"));
            var now = (await harness.SnapshotsAsync(p => p.GetCurrentAsync(organization, CancellationToken.None)))!.Find("ARCH-001")!;
            Assert.Equal(1, now.Revision);
            Assert.Equal(V1, now.Record.Title);

            Assert.Null(await harness.SnapshotsAsync(p => p.GetAsync(organization, "gs_00000000000000000000000000000000", CancellationToken.None)));
        }
    }

    private static async Task<SyncResult> RunIncrementallyAsync(GovernanceHarness harness, GovernanceRepo repo, GovernanceSourceConfig config)
    {
        SyncResult? last = null;
        for (var i = 0; i < History.Length; i++)
        {
            History[i](repo);
            repo.Commit($"step {i + 1}");
            if (SyncPoints.Contains(i + 1))
            {
                last = await harness.SyncAsync(config);
            }
        }

        return last!;
    }

    /// <summary>Everything a receipt replay depends on: which revision of which record, with what content, from where.</summary>
    private static string Fingerprint(GovernanceSnapshot snapshot) =>
        string.Join('\n', snapshot.Revisions.Select(r =>
            $"{r.Id}|rev {r.Revision}|{r.Record.ContentHash}|{r.Record.Status}|{r.Record.Title}|{r.Provenance.Repository}|{r.Provenance.CommitSha}|{r.Provenance.Path}|{r.Provenance.BlobSha}"));
}
