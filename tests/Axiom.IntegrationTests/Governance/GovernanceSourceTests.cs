using Axiom.Application.Governance;
using Axiom.Infrastructure.Governance;
using Axiom.Infrastructure.Governance.Git;
using Axiom.Infrastructure.Governance.Persistence;
using Axiom.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Axiom.IntegrationTests.Governance;

/// <summary>The Git source reader (ADR-0001, task 1.6) and the source registry.</summary>
[Collection(PostgresTests.Name)]
public class GovernanceSourceTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Reads_head_tree_history_and_changes_relative_to_the_repository_root()
    {
        using var repo = new GovernanceRepo();
        await using var harness = new GovernanceHarness(postgres);
        var source = harness.Services.GetRequiredService<IGovernanceSource>();
        var commits = harness.Services.GetRequiredService<IGovernanceCommitSource>();
        var first = repo.Write("governance/decisions/ARCH-001.yaml", "one")
            .Write("governance/schemas/decision.schema.json", "{}")
            .Write("governanceX/decisions/ARCH-777.yaml", "sibling directory sharing the root's prefix")
            .Write("README.md", "outside the root")
            .Commit("first");
        var second = repo.Write("governance/decisions/ARCH-001.yaml", "two").Write("README.md", "still outside").Commit("second");
        var third = repo.Move("governance/decisions/ARCH-001.yaml", "governance/decisions/nested/ARCH-001.yaml").Commit("third");
        // A file:// URL addresses the same repository as its path.
        var config = new GovernanceSourceConfig(PostgresFixture.NewOrganization(), new Uri(repo.Location).AbsoluteUri, repo.Branch, "governance/");

        var head = await source.FetchHeadAsync(config, CancellationToken.None);

        Assert.Equal(third, head.Sha);
        Assert.Equal("Ada Lovelace <ada@example.test>", head.Author);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 8, 3, 0, TimeSpan.Zero), head.CommittedAt);

        var tree = await source.ReadTreeAsync(config, first, CancellationToken.None);
        Assert.Equal(["governance/decisions/ARCH-001.yaml", "governance/schemas/decision.schema.json"], tree.Select(f => f.Path));
        Assert.Equal("one", tree[0].Content);
        Assert.Equal(40, tree[0].BlobSha.Length);
        Assert.Equal(["governance/decisions/nested/ARCH-001.yaml", "governance/schemas/decision.schema.json"], (await source.ReadTreeAsync(config, third, CancellationToken.None)).Select(f => f.Path));
        Assert.Equal(4, (await source.ReadTreeAsync(config with { RootPath = "" }, first, CancellationToken.None)).Count);
        Assert.Empty(await source.ReadTreeAsync(config with { RootPath = "missing" }, first, CancellationToken.None));

        // Changes are limited to the root; a rename reports both paths.
        Assert.Equal(["governance/decisions/ARCH-001.yaml"], await source.ChangedPathsAsync(config, first, second, CancellationToken.None));
        Assert.Equal(["governance/decisions/ARCH-001.yaml", "governance/decisions/nested/ARCH-001.yaml"], await source.ChangedPathsAsync(config, first, third, CancellationToken.None));
        Assert.Empty((await source.ChangedPathsAsync(config, second, second, CancellationToken.None))!);
        // Not an ancestor: the direction is wrong, or the commit is unknown.
        Assert.Null(await source.ChangedPathsAsync(config, third, first, CancellationToken.None));
        Assert.Null(await source.ChangedPathsAsync(config, new string('0', 40), third, CancellationToken.None));

        var history = await source.ReadHistoryAsync(config, second, "governance/decisions/ARCH-001.yaml", CancellationToken.None);
        Assert.Equal([(first, "one"), (second, "two")], history.Select(v => (v.Commit.Sha, v.Content)));
        Assert.Equal(tree[0].BlobSha, history[0].BlobSha);
        Assert.Empty(await source.ReadHistoryAsync(config, third, "README.md", CancellationToken.None));

        Assert.Equal([first, second, third], (await commits.ListFirstParentCommitsAsync(config, null, third, CancellationToken.None))!.Select(c => c.Sha));
        Assert.Equal([second, third], (await commits.ListFirstParentCommitsAsync(config, first, third, CancellationToken.None))!.Select(c => c.Sha));
        Assert.Empty((await commits.ListFirstParentCommitsAsync(config, third, third, CancellationToken.None))!);
        Assert.Null(await commits.ListFirstParentCommitsAsync(config, third, first, CancellationToken.None));

        var files = await commits.ReadFilesAsync(config, second, ["governance/decisions/ARCH-001.yaml", "governance/decisions/gone.yaml", "README.md"], CancellationToken.None);
        Assert.Equal("two", Assert.Single(files).Content);
    }

    [Fact]
    public async Task A_missing_branch_or_unreachable_repository_fails_without_touching_the_projection()
    {
        using var repo = new GovernanceRepo();
        await using var harness = new GovernanceHarness(postgres);
        var organization = PostgresFixture.NewOrganization();
        repo.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", "Health endpoints")).Commit("first");
        var config = repo.ConfigFor(organization);
        var published = await harness.SyncAsync(config);

        var missingBranch = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.SyncAsync(config with { Branch = "no-such-branch" }, SyncMode.Rebuild));
        Assert.Contains("no-such-branch", missingBranch.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.SyncAsync(config with { RepositoryUrl = Path.Combine(Path.GetTempPath(), "axiom-tests", "does-not-exist-" + Guid.NewGuid().ToString("N")) }, SyncMode.Rebuild));

        // design.md §16: when Git is unavailable the last published snapshot stays readable.
        var info = await harness.SnapshotsAsync(p => p.GetCurrentInfoAsync(organization, CancellationToken.None));
        Assert.Equal(published.SnapshotId, info!.SnapshotId);
        Assert.Equal(1, (await harness.SearchAsync(organization, new GovernanceQuery())).Total);
    }

    [Fact]
    public async Task Local_repositories_and_unsupported_or_credentialed_urls_are_refused()
    {
        using var repo = new GovernanceRepo();
        repo.Write(Records.PathOf("ARCH-001"), Records.Decision("ARCH-001", "Health endpoints")).Commit("first");
        var organization = PostgresFixture.NewOrganization();

        await using (var locked = new GovernanceHarness(postgres, allowLocalRepositories: false))
        {
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => locked.SyncAsync(repo.ConfigFor(organization)));
            Assert.Contains(nameof(GovernanceOptions.AllowLocalRepositories), refused.Message, StringComparison.Ordinal);
        }

        await using var harness = new GovernanceHarness(postgres);
        var source = harness.Services.GetRequiredService<IGovernanceSource>();
        foreach (var url in new[] { "http://git.example.test/gov.git", "ssh://git.example.test/gov.git", "git@git.example.test:org/gov.git", "relative/path" })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => source.FetchHeadAsync(new GovernanceSourceConfig(organization, url, "main", "governance"), CancellationToken.None));
        }

        // A URL carrying a secret is refused before anything is fetched or stored, and the secret is not echoed.
        var withSecret = new GovernanceSourceConfig(organization, "https://bot:s3cr3t-token@git.example.test/gov.git", "main", "governance");
        var syncError = await Assert.ThrowsAsync<ArgumentException>(() => harness.SyncAsync(withSecret));
        Assert.DoesNotContain("s3cr3t", syncError.Message, StringComparison.Ordinal);
        var registryError = await Assert.ThrowsAsync<ArgumentException>(() =>
            harness.InScopeAsync(async s =>
            {
                await s.GetRequiredService<IGovernanceSourceRegistry>().UpsertAsync(withSecret, CancellationToken.None);
                return 0;
            }));
        Assert.DoesNotContain("s3cr3t", registryError.Message, StringComparison.Ordinal);
        await using var db = harness.Db();
        Assert.False(await db.Set<GovernanceSourceRow>().AnyAsync(s => s.OrganizationId == organization));
        Assert.False(await db.Set<SyncCheckpointRow>().AnyAsync(s => s.OrganizationId == organization));
    }

    [Fact]
    public async Task Tokens_are_offered_only_to_the_https_host_they_are_configured_for()
    {
        var options = new GovernanceOptions();
        options.Credentials["git.example.test"] = new GovernanceHostCredential { Token = "s3cr3t-token" };
        options.Credentials["no-token.example.test"] = new GovernanceHostCredential();
        var credentials = new ConfiguredGovernanceSourceCredentials(new StaticOptions(options));
        ValueTask<GovernanceSourceCredential?> Resolve(string url) =>
            credentials.ResolveAsync(new GovernanceSourceConfig("acme", url, "main", "governance"), CancellationToken.None);

        var matched = await Resolve("https://GIT.example.test/acme/governance.git");

        Assert.Equal(new GovernanceSourceCredential("x-access-token", "s3cr3t-token"), matched);
        Assert.DoesNotContain("s3cr3t", matched!.ToString(), StringComparison.Ordinal);
        Assert.Null(await Resolve("https://other.example.test/acme/governance.git"));
        Assert.Null(await Resolve("https://git.example.test.evil.test/acme/governance.git"));
        Assert.Null(await Resolve("http://git.example.test/acme/governance.git"));
        Assert.Null(await Resolve("https://no-token.example.test/acme/governance.git"));
        Assert.Null(await Resolve("/srv/git/governance.git"));
    }

    [Fact]
    public async Task Source_registry_stores_one_source_per_organization()
    {
        await using var harness = new GovernanceHarness(postgres);
        var organizationA = PostgresFixture.NewOrganization();
        var organizationB = PostgresFixture.NewOrganization();
        Task Upsert(GovernanceSourceConfig config) => harness.InScopeAsync(async s =>
        {
            await s.GetRequiredService<IGovernanceSourceRegistry>().UpsertAsync(config, CancellationToken.None);
            return 0;
        });
        Task<GovernanceSourceConfig?> Find(string organization) =>
            harness.InScopeAsync(s => s.GetRequiredService<IGovernanceSourceRegistry>().FindAsync(organization, CancellationToken.None));

        await Upsert(new GovernanceSourceConfig(organizationA, "https://git.example.test/a/governance.git", "main", "governance/"));
        await Upsert(new GovernanceSourceConfig(organizationB, "https://git.example.test/b/governance.git", "trunk", ""));
        await Upsert(new GovernanceSourceConfig(organizationA, "https://git.example.test/a/governance.git", "release", "records"));

        Assert.Equal(new GovernanceSourceConfig(organizationA, "https://git.example.test/a/governance.git", "release", "records"), await Find(organizationA));
        Assert.Equal(new GovernanceSourceConfig(organizationB, "https://git.example.test/b/governance.git", "trunk", ""), await Find(organizationB));
        Assert.Null(await Find(PostgresFixture.NewOrganization()));
        var listed = await harness.InScopeAsync(s => s.GetRequiredService<IGovernanceSourceRegistry>().ListAsync(CancellationToken.None));
        Assert.Single(listed, c => c.OrganizationId == organizationA);
        Assert.Single(listed, c => c.OrganizationId == organizationB);
    }

    private sealed class StaticOptions(GovernanceOptions value) : IOptionsMonitor<GovernanceOptions>
    {
        public GovernanceOptions CurrentValue => value;

        public GovernanceOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<GovernanceOptions, string?> listener) => null;
    }
}
