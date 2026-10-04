using System.Text.Json;
using Axiom.Application.Governance;
using Axiom.Infrastructure.Governance;
using Axiom.Infrastructure.Governance.Persistence;
using Axiom.Infrastructure.Persistence;
using Axiom.IntegrationTests.Support;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Axiom.IntegrationTests.Governance;

/// <summary>A real Git repository in a temporary directory, standing in for an organization's governance repository.</summary>
internal sealed class GovernanceRepo : IDisposable
{
    private readonly Repository _repository;
    private DateTimeOffset _clock = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    public GovernanceRepo()
    {
        Location = Path.Combine(Path.GetTempPath(), "axiom-tests", "repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Location);
        Repository.Init(Location);
        _repository = new Repository(Location);
    }

    public string Location { get; }

    public string Branch => _repository.Head.FriendlyName;

    public GovernanceSourceConfig ConfigFor(string organizationId, string rootPath = "governance") =>
        new(organizationId, Location, Branch, rootPath);

    public GovernanceRepo Write(string relativePath, string content)
    {
        var full = Path.Combine(Location, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return this;
    }

    public GovernanceRepo Delete(string relativePath)
    {
        File.Delete(Path.Combine(Location, relativePath));
        return this;
    }

    public GovernanceRepo Move(string fromPath, string toPath)
    {
        var target = Path.Combine(Location, toPath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(Path.Combine(Location, fromPath), target);
        return this;
    }

    public string Commit(string message)
    {
        Commands.Stage(_repository, "*");
        _clock = _clock.AddMinutes(1);
        var signature = new Signature("Ada Lovelace", "ada@example.test", _clock);
        return _repository.Commit(message, signature, signature, new CommitOptions { AllowEmptyCommit = true }).Sha;
    }

    /// <summary>Moves the branch back to <paramref name="sha"/>, discarding later commits (a force-push in effect).</summary>
    public void ResetHard(string sha) => _repository.Reset(ResetMode.Hard, _repository.Lookup<Commit>(sha));

    public void Dispose()
    {
        _repository.Dispose();
        ForceDelete(Location);
    }

    internal static void ForceDelete(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(directory, recursive: true);
    }
}

/// <summary>The governance module wired exactly as a host wires it, against the shared test database.</summary>
internal sealed class GovernanceHarness : IAsyncDisposable
{
    private static readonly JsonSerializerOptions DumpJson = new() { WriteIndented = true };
    private readonly PostgresFixture _postgres;
    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(), "axiom-tests", "mirrors-" + Guid.NewGuid().ToString("N"));

    public GovernanceHarness(PostgresFixture postgres, bool allowLocalRepositories = true)
    {
        _postgres = postgres;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{GovernanceOptions.SectionName}:{nameof(GovernanceOptions.CacheDirectory)}"] = _cacheDirectory,
                [$"{GovernanceOptions.SectionName}:{nameof(GovernanceOptions.AllowLocalRepositories)}"] = allowLocalRepositories ? "true" : "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(Time);
        services.AddDbContext<AxiomDbContext>(options => options.UseNpgsql(postgres.ConnectionString, npgsql => npgsql.UseVector()));
        services.AddGovernanceInfrastructure(configuration);
        Services = services.BuildServiceProvider(validateScopes: true);
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero));

    public ServiceProvider Services { get; }

    public AxiomDbContext Db() => _postgres.CreateContext();

    public async Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    public Task<SyncResult> SyncAsync(GovernanceSourceConfig config, SyncMode mode = SyncMode.Incremental) =>
        InScopeAsync(services => services.GetRequiredService<IGovernanceSynchronizer>().SynchronizeAsync(config, mode, CancellationToken.None));

    public Task<GovernanceSearchResult> SearchAsync(string organizationId, GovernanceQuery query) =>
        InScopeAsync(services => services.GetRequiredService<IGovernanceQueries>().SearchAsync(organizationId, query, CancellationToken.None));

    public Task<GovernanceDetail?> GetAsync(string organizationId, string recordId) =>
        InScopeAsync(services => services.GetRequiredService<IGovernanceQueries>().GetAsync(organizationId, recordId, CancellationToken.None));

    public Task<T> SnapshotsAsync<T>(Func<IGovernanceSnapshotProvider, Task<T>> work) =>
        InScopeAsync(services => work(services.GetRequiredService<IGovernanceSnapshotProvider>()));

    public async Task<List<OutboxEvent>> EventsAsync(string organizationId, string? eventType = null)
    {
        await using var db = Db();
        return await db.Set<OutboxEvent>().AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && (eventType == null || e.EventType == eventType))
            .OrderBy(e => e.EventId)
            .ToListAsync();
    }

    public async Task<List<GovernanceSnapshotRow>> SnapshotRowsAsync(string organizationId)
    {
        await using var db = Db();
        return await db.Set<GovernanceSnapshotRow>().AsNoTracking().Where(s => s.OrganizationId == organizationId).OrderBy(s => s.Sequence).ToListAsync();
    }

    /// <summary>
    /// Every projected row of the organization as text, minus the two wall-clock columns
    /// (<c>published_at</c>, <c>updated_at</c>) that record when the projection was written rather than what it contains.
    /// </summary>
    public async Task<string> DumpProjectionAsync(string organizationId)
    {
        await using var db = Db();
        var dump = new
        {
            records = await db.Set<GovernanceRecordRow>().AsNoTracking().Where(r => r.OrganizationId == organizationId).OrderBy(r => r.RecordId)
                .Select(r => new { r.RecordId, r.Kind, r.Status, r.Title, r.Statement, r.Owners, r.Tags, r.TagsText, r.ReviewAfter, r.Revision, r.ContentHash, r.Path, r.BlobSha, r.CommitSha, r.CommittedAt, r.Author })
                .ToListAsync(),
            revisions = await db.Set<GovernanceRevisionRow>().AsNoTracking().Where(r => r.OrganizationId == organizationId).OrderBy(r => r.RecordId).ThenBy(r => r.Revision).ToListAsync(),
            scopes = await db.Set<GovernanceScopeRow>().AsNoTracking().Where(r => r.OrganizationId == organizationId).OrderBy(r => r.RecordId).ThenBy(r => r.Dimension).ThenBy(r => r.ValueKey).ToListAsync(),
            relations = await db.Set<GovernanceRelationRow>().AsNoTracking().Where(r => r.OrganizationId == organizationId).OrderBy(r => r.RecordId).ThenBy(r => r.Kind).ThenBy(r => r.TargetId).ToListAsync(),
            snapshots = await db.Set<GovernanceSnapshotRow>().AsNoTracking().Where(r => r.OrganizationId == organizationId).OrderBy(r => r.Sequence)
                .Select(r => new { r.SourceCommit, r.Sequence, r.SnapshotId, r.RecordCount, r.WarningCount, r.CommittedAt, r.Author })
                .ToListAsync(),
            entries = await db.Set<GovernanceSnapshotEntryRow>().AsNoTracking().Where(r => r.OrganizationId == organizationId).OrderBy(r => r.RecordId).ThenBy(r => r.FromSequence).ToListAsync(),
            checkpoint = await db.Set<SyncCheckpointRow>().AsNoTracking().Where(r => r.OrganizationId == organizationId)
                .Select(r => new { r.RepositoryUrl, r.Branch, r.RootPath, r.HeadCommit, r.HeadOutcome, r.PublishedCommit, r.PublishedSequence, r.SnapshotId, r.RecordCount, r.Issues })
                .ToListAsync(),
        };
        return JsonSerializer.Serialize(dump, DumpJson);
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        GovernanceRepo.ForceDelete(_cacheDirectory);
    }
}

/// <summary>Governance source files used as fixtures.</summary>
internal static class Records
{
    public const string GatewayDecisionPath = "governance/decisions/ARCH-042-gateway-routing.md";
    public const string LegacyExceptionPath = "governance/exceptions/EXC-023-legacy-homepage.yaml";

    /// <summary>A record from the specification's examples (<c>examples/governance</c>).</summary>
    public static string SpecExample(string relativePath) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SpecExamples", relativePath));

    public static GovernanceRepo WithSpecExamples(this GovernanceRepo repo) => repo
        .Write(GatewayDecisionPath, SpecExample("decisions/ARCH-042-gateway-routing.md"))
        .Write(LegacyExceptionPath, SpecExample("exceptions/EXC-023-legacy-homepage.yaml"));

    public static string PathOf(string id) => $"governance/decisions/{id}.yaml";

    public static string Decision(
        string id,
        string title,
        string status = "accepted",
        string owner = "platform-architecture",
        string tags = "",
        string scope = "{}",
        string statement = "Services expose health endpoints.",
        string relationships = "",
        string reviewAfter = "")
    {
        var text = $"""
            schemaVersion: axiom.io/v1
            kind: Decision
            metadata:
              id: {id}
              title: {title}
              owners: [{owner}]
              tags: [{tags}]
            spec:
              status: {status}
              scope: {scope}
              authority:
                level: system
                exemptable: true
              enforcement:
                defaultVerdict: warn
              decision: {statement}

            """;
        if (relationships.Length > 0)
        {
            text += $"  relationships: {relationships}\n";
        }

        if (reviewAfter.Length > 0)
        {
            text += $"  validity:\n    reviewAfter: {reviewAfter}\n";
        }

        return text;
    }
}
