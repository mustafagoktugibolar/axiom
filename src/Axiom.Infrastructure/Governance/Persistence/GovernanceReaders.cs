using System.Collections.Immutable;
using Axiom.Application.Governance;
using Axiom.Domain.Governance;
using Axiom.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Axiom.Infrastructure.Governance.Persistence;

/// <summary>
/// Turns stored revision source text back into the domain model through the one parser, so the
/// projection never has a second mapping of a record. Parsed revisions are cached by content hash
/// (design.md §17): the same path and content always parse to the same record.
/// </summary>
public sealed class GovernanceRevisionReader(IGovernanceRecordParser parser, IMemoryCache cache)
{
    private static readonly MemoryCacheEntryOptions CacheEntry = new() { Size = 1, SlidingExpiration = TimeSpan.FromHours(1) };

    public GovernanceRecord Read(string path, string contentHash, string sourceText)
    {
        var key = (nameof(GovernanceRevisionReader), path, contentHash);
        if (cache.TryGetValue(key, out GovernanceRecord? cached) && cached is not null)
        {
            return cached;
        }

        var parsed = parser.Parse(path, sourceText);
        if (parsed.Record is null || !string.Equals(parsed.Record.ContentHash, contentHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The stored source of '{path}' ({contentHash}) can no longer be read as a governance record. Rebuild the projection from Git.");
        }

        return cache.Set(key, parsed.Record, CacheEntry);
    }
}

/// <summary>
/// Reads published snapshots from the projection only, so they stay readable when Git is unreachable
/// (design.md §16). A snapshot is immutable once published and is cached by organization and ID.
/// </summary>
public sealed class EfGovernanceSnapshotProvider(AxiomDbContext db, GovernanceRevisionReader reader, IMemoryCache cache) : IGovernanceSnapshotProvider
{
    private static readonly MemoryCacheEntryOptions CacheEntry = new() { Size = 1, SlidingExpiration = TimeSpan.FromHours(1) };

    public async Task<SnapshotInfo?> GetCurrentInfoAsync(string organizationId, CancellationToken cancellationToken) =>
        await CurrentRowAsync(organizationId, cancellationToken) is { } row ? ToInfo(row) : null;

    public async Task<GovernanceSnapshot?> GetCurrentAsync(string organizationId, CancellationToken cancellationToken)
    {
        var row = await CurrentRowAsync(organizationId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        // Commits that do not touch a record share a snapshot ID; the current snapshot reports the commit it is current for.
        var key = (nameof(EfGovernanceSnapshotProvider), organizationId, row.SnapshotId, row.SourceCommit);
        if (cache.TryGetValue(key, out GovernanceSnapshot? cached) && cached is not null)
        {
            return cached;
        }

        return cache.Set(key, await LoadAsync(row, cancellationToken), CacheEntry);
    }

    public async Task<GovernanceSnapshot?> GetAsync(string organizationId, string snapshotId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        var key = (nameof(EfGovernanceSnapshotProvider), organizationId, snapshotId);
        if (cache.TryGetValue(key, out GovernanceSnapshot? cached) && cached is not null)
        {
            return cached;
        }

        // The first commit that published this content identifies the snapshot; a rebuild finds the same one.
        var row = await db.Set<GovernanceSnapshotRow>().AsNoTracking()
            .Where(s => s.OrganizationId == organizationId && s.SnapshotId == snapshotId)
            .OrderBy(s => s.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : cache.Set(key, await LoadAsync(row, cancellationToken), CacheEntry);
    }

    private static SnapshotInfo ToInfo(GovernanceSnapshotRow row) =>
        new(row.SnapshotId, row.OrganizationId, row.SourceCommit, row.PublishedAt, row.RecordCount);

    private async Task<GovernanceSnapshotRow?> CurrentRowAsync(string organizationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        return await (
            from checkpoint in db.Set<SyncCheckpointRow>().AsNoTracking()
            where checkpoint.OrganizationId == organizationId && checkpoint.PublishedCommit != null
            join snapshot in db.Set<GovernanceSnapshotRow>().AsNoTracking()
                on new { checkpoint.OrganizationId, Commit = checkpoint.PublishedCommit! }
                equals new { snapshot.OrganizationId, Commit = snapshot.SourceCommit }
            select snapshot)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<GovernanceSnapshot> LoadAsync(GovernanceSnapshotRow row, CancellationToken cancellationToken)
    {
        var organizationId = row.OrganizationId;
        var repository = await db.Set<SyncCheckpointRow>().AsNoTracking()
            .Where(c => c.OrganizationId == organizationId)
            .Select(c => c.RepositoryUrl)
            .SingleAsync(cancellationToken);

        var entries = db.Set<GovernanceSnapshotEntryRow>().AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && e.FromSequence <= row.Sequence);
        var members = await (
            from entry in entries
            where entry.Revision != null
                && entry.FromSequence == entries.Where(other => other.RecordId == entry.RecordId).Max(other => other.FromSequence)
            join revision in db.Set<GovernanceRevisionRow>().AsNoTracking()
                on new { entry.OrganizationId, entry.RecordId, Revision = entry.Revision!.Value }
                equals new { revision.OrganizationId, revision.RecordId, revision.Revision }
            select new { entry.RecordId, revision.Revision, revision.ContentHash, revision.SourceText, entry.Path, entry.BlobSha, entry.CommitSha })
            .ToListAsync(cancellationToken);

        var snapshot = GovernanceSnapshot.Create(
            organizationId,
            row.SourceCommit,
            members.Select(member => new RecordRevision(
                reader.Read(member.Path!, member.ContentHash, member.SourceText),
                new SourceProvenance(repository, member.CommitSha, member.Path!, member.BlobSha!),
                member.Revision)));

        if (!string.Equals(snapshot.Id, row.SnapshotId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Snapshot '{row.SnapshotId}' of organization '{organizationId}' no longer matches its stored revisions (recomputed '{snapshot.Id}'). Rebuild the projection from Git.");
        }

        return snapshot;
    }
}

/// <summary>Search and read access over the current governance projection (R19).</summary>
public sealed class EfGovernanceQueries(AxiomDbContext db, GovernanceRevisionReader reader, TimeProvider timeProvider) : IGovernanceQueries
{
    public const int MaxPageSize = 500;

    public async Task<GovernanceSearchResult> SearchAsync(string organizationId, GovernanceQuery query, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentNullException.ThrowIfNull(query);
        var today = Today();
        var records = db.Set<GovernanceRecordRow>().AsNoTracking().Where(r => r.OrganizationId == organizationId);
        var text = string.IsNullOrWhiteSpace(query.Text) ? null : query.Text.Trim();

        if (text is not null)
        {
            records = records.Where(r =>
                r.SearchVector.Matches(EF.Functions.WebSearchToTsQuery(GovernanceRecordRowConfiguration.SearchConfig, text)) || r.RecordId == text);
        }

        if (!query.Ids.IsDefaultOrEmpty)
        {
            var ids = query.Ids.ToArray();
            records = records.Where(r => ids.Contains(r.RecordId));
        }

        if (!query.Kinds.IsDefaultOrEmpty)
        {
            var kinds = query.Kinds.ToArray();
            records = records.Where(r => kinds.Contains(r.Kind));
        }

        if (!query.Statuses.IsDefaultOrEmpty)
        {
            var statuses = query.Statuses.ToArray();
            records = records.Where(r => statuses.Contains(r.Status));
        }

        if (!string.IsNullOrWhiteSpace(query.Owner))
        {
            var owner = query.Owner.Trim();
            records = records.Where(r => r.Owners.Contains(owner));
        }

        if (!string.IsNullOrWhiteSpace(query.Tag))
        {
            var tag = query.Tag.Trim();
            records = records.Where(r => r.Tags.Contains(tag));
        }

        foreach (var (dimension, value) in query.Scope)
        {
            var key = GovernanceScopeRow.KeyOf(dimension, value);
            records = records.Where(r => db.Set<GovernanceScopeRow>().Any(s =>
                s.OrganizationId == organizationId && s.RecordId == r.RecordId && s.Dimension == dimension && s.ValueKey == key));
        }

        if (query.RelationKind is not null || !string.IsNullOrWhiteSpace(query.RelatedTo))
        {
            var kind = query.RelationKind;
            var target = string.IsNullOrWhiteSpace(query.RelatedTo) ? null : query.RelatedTo.Trim();
            var relations = db.Set<GovernanceRelationRow>().Where(x => x.OrganizationId == organizationId);
            if (kind is not null)
            {
                relations = relations.Where(x => x.Kind == kind.Value);
            }

            if (target is not null)
            {
                relations = relations.Where(x => x.TargetId == target);
            }

            records = records.Where(r => relations.Any(x => x.RecordId == r.RecordId));
        }

        if (query.StaleOnly)
        {
            // R21: staleness is a flag on a record that still governs, so only accepted records can be stale.
            records = records.Where(r => r.Status == LifecycleStatus.Accepted && r.ReviewAfter != null && r.ReviewAfter < today);
        }

        var total = await records.CountAsync(cancellationToken);
        var take = Math.Clamp(query.Take, 0, MaxPageSize);
        if (take == 0 || total == 0)
        {
            return new GovernanceSearchResult([], total);
        }

        var ordered = text is null
            ? records.OrderBy(r => r.RecordId)
            : records
                .OrderByDescending(r => r.SearchVector.Rank(EF.Functions.WebSearchToTsQuery(GovernanceRecordRowConfiguration.SearchConfig, text)))
                .ThenBy(r => r.RecordId);

        var page = await (
            from record in ordered.Skip(Math.Max(query.Skip, 0)).Take(take)
            join revision in db.Set<GovernanceRevisionRow>().AsNoTracking()
                on new { record.OrganizationId, record.RecordId, record.Revision }
                equals new { revision.OrganizationId, revision.RecordId, revision.Revision }
            select new { record.RecordId, record.Revision, record.ContentHash, record.Path, record.CommitSha, revision.SourceText })
            .ToListAsync(cancellationToken);

        // The join does not promise to keep the page order, so it is restored from the ordered keys.
        var order = await ordered.Skip(Math.Max(query.Skip, 0)).Take(take).Select(r => r.RecordId).ToListAsync(cancellationToken);
        var byId = page.ToDictionary(r => r.RecordId, StringComparer.Ordinal);

        return new GovernanceSearchResult(
            [.. order.Where(byId.ContainsKey).Select(id => byId[id]).Select(row =>
            {
                var record = reader.Read(row.Path, row.ContentHash, row.SourceText);
                return new GovernanceSummary(
                    record.Id,
                    record.Kind,
                    record.Title,
                    record.Status,
                    record.IsAuthoritativeOn(today),
                    record.Owners,
                    record.Tags,
                    record.Authority.Level,
                    record.Authority.Exemptable,
                    row.Revision,
                    record.Validity.ReviewAfter,
                    IsStale(record, today),
                    row.Path,
                    row.CommitSha);
            })],
            total);
    }

    public async Task<GovernanceDetail?> GetAsync(string organizationId, string recordId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        var current = await (
            from record in db.Set<GovernanceRecordRow>().AsNoTracking()
            where record.OrganizationId == organizationId && record.RecordId == recordId
            join revision in db.Set<GovernanceRevisionRow>().AsNoTracking()
                on new { record.OrganizationId, record.RecordId, record.Revision }
                equals new { revision.OrganizationId, revision.RecordId, revision.Revision }
            select new { record.Revision, record.ContentHash, record.Path, record.BlobSha, record.CommitSha, revision.SourceText })
            .SingleOrDefaultAsync(cancellationToken);
        if (current is null)
        {
            return null;
        }

        var repository = await RepositoryAsync(organizationId, cancellationToken);
        var history = await db.Set<GovernanceRevisionRow>().AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.RecordId == recordId)
            .OrderBy(r => r.Revision)
            .Select(r => new RevisionInfo(r.Revision, r.ContentHash, r.Status, r.CommitSha, r.CommittedAt, r.Author, r.Path))
            .ToListAsync(cancellationToken);
        var incoming = await db.Set<GovernanceRelationRow>().AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.TargetId == recordId && x.RecordId != recordId)
            .OrderBy(x => x.RecordId)
            .ThenBy(x => x.Kind)
            .Select(x => new { x.Kind, x.RecordId })
            .ToListAsync(cancellationToken);

        return new GovernanceDetail(
            new RecordRevision(
                reader.Read(current.Path, current.ContentHash, current.SourceText),
                new SourceProvenance(repository, current.CommitSha, current.Path, current.BlobSha),
                current.Revision),
            repository,
            [.. history],
            // For an incoming relation the "target" is the record that declares it.
            [.. incoming.Select(x => new Relation(x.Kind, x.RecordId))]);
    }

    public async Task<RecordRevision?> GetRevisionAsync(string organizationId, string recordId, int revision, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        var row = await db.Set<GovernanceRevisionRow>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.OrganizationId == organizationId && r.RecordId == recordId && r.Revision == revision, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var repository = await RepositoryAsync(organizationId, cancellationToken);
        return new RecordRevision(
            reader.Read(row.Path, row.ContentHash, row.SourceText),
            new SourceProvenance(repository, row.CommitSha, row.Path, row.BlobSha),
            row.Revision);
    }

    internal static bool IsStale(GovernanceRecord record, DateOnly today) =>
        record.Status == LifecycleStatus.Accepted && record.Validity.IsReviewOverdueOn(today);

    private DateOnly Today() => DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

    private Task<string> RepositoryAsync(string organizationId, CancellationToken cancellationToken) =>
        db.Set<SyncCheckpointRow>().AsNoTracking()
            .Where(c => c.OrganizationId == organizationId)
            .Select(c => c.RepositoryUrl)
            .SingleAsync(cancellationToken);
}

/// <summary>Registered governance sources. Only the location is stored; credentials never are.</summary>
public sealed class EfGovernanceSourceRegistry(AxiomDbContext db, TimeProvider timeProvider) : IGovernanceSourceRegistry
{
    public async Task<IReadOnlyList<GovernanceSourceConfig>> ListAsync(CancellationToken cancellationToken) =>
        await db.Set<GovernanceSourceRow>().AsNoTracking()
            .OrderBy(s => s.OrganizationId)
            .Select(s => new GovernanceSourceConfig(s.OrganizationId, s.RepositoryUrl, s.Branch, s.RootPath))
            .ToListAsync(cancellationToken);

    public async Task<GovernanceSourceConfig?> FindAsync(string organizationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        return await db.Set<GovernanceSourceRow>().AsNoTracking()
            .Where(s => s.OrganizationId == organizationId)
            .Select(s => new GovernanceSourceConfig(s.OrganizationId, s.RepositoryUrl, s.Branch, s.RootPath))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task UpsertAsync(GovernanceSourceConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.OrganizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.RepositoryUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Branch);
        if (GovernanceSourceUrl.HasEmbeddedCredentials(config.RepositoryUrl))
        {
            throw new ArgumentException("The governance repository URL must not embed credentials; they are never stored.", nameof(config));
        }

        var rootPath = GlobPattern.NormalizePath(config.RootPath ?? string.Empty);
        var row = await db.Set<GovernanceSourceRow>().SingleOrDefaultAsync(s => s.OrganizationId == config.OrganizationId, cancellationToken);
        if (row is null)
        {
            row = new GovernanceSourceRow { OrganizationId = config.OrganizationId, RepositoryUrl = config.RepositoryUrl, Branch = config.Branch, RootPath = rootPath };
            db.Add(row);
        }

        row.RepositoryUrl = config.RepositoryUrl;
        row.Branch = config.Branch;
        row.RootPath = rootPath;
        row.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }
}
