using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Axiom.Application.Common;
using Axiom.Application.Governance;
using Axiom.Domain.Governance;
using Axiom.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Axiom.Infrastructure.Governance.Persistence;

/// <summary>PostgreSQL advisory lock keys of the governance module, derived from the organization.</summary>
internal static class GovernanceLocks
{
    public static long Synchronization(string organizationId) => Key("axiom.governance.sync", organizationId);

    public static long Notification(string organizationId) => Key("axiom.governance.notify", organizationId);

    private static long Key(string purpose, string organizationId) =>
        BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes($"{purpose}\n{organizationId}")));
}

/// <summary>
/// Opens write sessions on the governance projection. Each session owns a context and a connection
/// of its own, on which it holds a session-level advisory lock for the organization, so two
/// synchronizers never interleave their walks.
/// </summary>
public sealed class EfGovernanceProjectionStore(DbContextOptions<AxiomDbContext> options, TimeProvider timeProvider) : IGovernanceProjectionStore
{
    public async Task<IGovernanceProjectionSession> OpenAsync(string organizationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        var db = new AxiomDbContext(options);
        try
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            var key = GovernanceLocks.Synchronization(organizationId);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock({key})", cancellationToken);
            return new Session(db, organizationId, key, timeProvider);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    private sealed class Session(AxiomDbContext db, string organizationId, long lockKey, TimeProvider timeProvider) : IGovernanceProjectionSession
    {
        private static readonly JsonSerializerOptions IssueJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

        public async Task<SyncCheckpoint?> GetCheckpointAsync(CancellationToken cancellationToken)
        {
            var row = await db.Set<SyncCheckpointRow>().AsNoTracking().SingleOrDefaultAsync(c => c.OrganizationId == organizationId, cancellationToken);
            return row is null
                ? null
                : new SyncCheckpoint(
                    row.RepositoryUrl,
                    row.Branch,
                    row.RootPath,
                    row.HeadCommit,
                    row.HeadOutcome,
                    row.PublishedCommit,
                    row.PublishedSequence,
                    row.SnapshotId,
                    row.RecordCount,
                    [.. JsonSerializer.Deserialize<ValidationIssue[]>(row.Issues, IssueJson) ?? []]);
        }

        public async Task<IReadOnlyList<PublishedRecordState>> LoadPublishedRecordsAsync(CancellationToken cancellationToken) =>
            await (
                from record in db.Set<GovernanceRecordRow>().AsNoTracking()
                where record.OrganizationId == organizationId
                join revision in db.Set<GovernanceRevisionRow>().AsNoTracking()
                    on new { record.OrganizationId, record.RecordId, record.Revision }
                    equals new { revision.OrganizationId, revision.RecordId, revision.Revision }
                orderby record.RecordId
                select new PublishedRecordState(record.RecordId, record.Revision, record.ContentHash, record.Path, record.BlobSha, record.CommitSha, revision.SourceText))
                .ToListAsync(cancellationToken);

        public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>> LoadRevisionNumbersAsync(IReadOnlyCollection<string> recordIds, CancellationToken cancellationToken)
        {
            var ids = recordIds.ToArray();
            var rows = await db.Set<GovernanceRevisionRow>().AsNoTracking()
                .Where(r => r.OrganizationId == organizationId && ids.Contains(r.RecordId))
                .Select(r => new { r.RecordId, r.ContentHash, r.Revision })
                .ToListAsync(cancellationToken);
            return rows
                .GroupBy(r => r.RecordId, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyDictionary<string, int>)g.ToDictionary(r => r.ContentHash, r => r.Revision, StringComparer.Ordinal),
                    StringComparer.Ordinal);
        }

        public Task ResetAsync(CancellationToken cancellationToken) =>
            InTransactionAsync(
                async () =>
                {
                    await db.Set<GovernanceScopeRow>().Where(r => r.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken);
                    await db.Set<GovernanceRelationRow>().Where(r => r.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken);
                    await db.Set<GovernanceRecordRow>().Where(r => r.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken);
                    await db.Set<GovernanceSnapshotEntryRow>().Where(r => r.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken);
                    await db.Set<GovernanceRevisionRow>().Where(r => r.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken);
                    await db.Set<GovernanceSnapshotRow>().Where(r => r.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken);
                    await db.Set<SyncCheckpointRow>().Where(r => r.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken);
                },
                cancellationToken);

        public Task PublishAsync(GovernanceSourceConfig config, PublishedCommit commit, CancellationToken cancellationToken) =>
            InTransactionAsync(
                async () =>
                {
                    var source = commit.Commit;
                    db.Add(new GovernanceSnapshotRow
                    {
                        OrganizationId = organizationId,
                        SourceCommit = source.Sha,
                        Sequence = commit.Sequence,
                        SnapshotId = commit.SnapshotId,
                        RecordCount = commit.RecordCount,
                        WarningCount = commit.Warnings.Length,
                        CommittedAt = source.CommittedAt,
                        Author = source.Author,
                        PublishedAt = commit.PublishedAt,
                    });

                    if (!commit.Changes.IsEmpty)
                    {
                        await ApplyChangesAsync(commit, cancellationToken);
                    }

                    var checkpoint = await UpsertCheckpointAsync(config, source, SyncOutcome.Published, commit.Warnings, cancellationToken);
                    checkpoint.PublishedCommit = source.Sha;
                    checkpoint.PublishedSequence = commit.Sequence;
                    checkpoint.SnapshotId = commit.SnapshotId;
                    checkpoint.RecordCount = commit.RecordCount;

                    // Same context, same transaction: the events commit with the change or not at all.
                    var outbox = new EfEventOutbox(db);
                    foreach (var integrationEvent in commit.Events)
                    {
                        outbox.Enqueue(integrationEvent);
                    }

                    await db.SaveChangesAsync(cancellationToken);
                },
                cancellationToken);

        public Task RejectAsync(GovernanceSourceConfig config, SourceCommit commit, ImmutableArray<ValidationIssue> issues, CancellationToken cancellationToken) =>
            InTransactionAsync(
                async () =>
                {
                    await UpsertCheckpointAsync(config, commit, SyncOutcome.Rejected, issues, cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                },
                cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                // Without this the pooled connection would carry the lock to its next user.
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({lockKey})");
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
            {
                // A broken connection has already released the lock on the server.
            }
            finally
            {
                await db.DisposeAsync();
            }
        }

        private async Task ApplyChangesAsync(PublishedCommit commit, CancellationToken cancellationToken)
        {
            var source = commit.Commit;
            var ids = commit.Changes.Select(change => change.RecordId).ToArray();
            var existing = await db.Set<GovernanceRecordRow>()
                .Where(r => r.OrganizationId == organizationId && ids.Contains(r.RecordId))
                .ToDictionaryAsync(r => r.RecordId, StringComparer.Ordinal, cancellationToken);
            await db.Set<GovernanceScopeRow>().Where(r => r.OrganizationId == organizationId && ids.Contains(r.RecordId)).ExecuteDeleteAsync(cancellationToken);
            await db.Set<GovernanceRelationRow>().Where(r => r.OrganizationId == organizationId && ids.Contains(r.RecordId)).ExecuteDeleteAsync(cancellationToken);

            foreach (var change in commit.Changes)
            {
                db.Add(new GovernanceSnapshotEntryRow
                {
                    OrganizationId = organizationId,
                    RecordId = change.RecordId,
                    FromSequence = commit.Sequence,
                    Revision = change.Revision,
                    Path = change.Path,
                    BlobSha = change.BlobSha,
                    CommitSha = source.Sha,
                });

                existing.TryGetValue(change.RecordId, out var row);
                if (change.Type == RecordChangeType.Removed)
                {
                    if (row is not null)
                    {
                        db.Remove(row);
                    }

                    continue;
                }

                var record = change.Record!;
                if (change.IsNewRevision)
                {
                    db.Add(new GovernanceRevisionRow
                    {
                        OrganizationId = organizationId,
                        RecordId = record.Id,
                        Revision = change.Revision!.Value,
                        ContentHash = record.ContentHash,
                        Kind = record.Kind,
                        Status = record.Status,
                        Title = record.Title,
                        Path = change.Path!,
                        BlobSha = change.BlobSha!,
                        CommitSha = source.Sha,
                        CommittedAt = source.CommittedAt,
                        Author = source.Author,
                        SourceText = change.SourceText!,
                    });
                }

                if (row is null)
                {
                    row = new GovernanceRecordRow
                    {
                        OrganizationId = organizationId,
                        RecordId = record.Id,
                        Title = record.Title,
                        Statement = record.Statement,
                        Owners = [],
                        Tags = [],
                        TagsText = string.Empty,
                        ContentHash = record.ContentHash,
                        Path = change.Path!,
                        BlobSha = change.BlobSha!,
                        CommitSha = source.Sha,
                        Author = source.Author,
                    };
                    db.Add(row);
                }

                row.Kind = record.Kind;
                row.Status = record.Status;
                row.Title = record.Title;
                row.Statement = record.Statement;
                row.Owners = [.. record.Owners];
                row.Tags = [.. record.Tags];
                row.TagsText = string.Join(' ', record.Tags);
                row.ReviewAfter = record.Validity.ReviewAfter;
                row.Revision = change.Revision!.Value;
                row.ContentHash = record.ContentHash;
                row.Path = change.Path!;
                row.BlobSha = change.BlobSha!;
                row.CommitSha = source.Sha;
                row.CommittedAt = source.CommittedAt;
                row.Author = source.Author;

                db.AddRange(record.Scope.RestrictedDimensions
                    .SelectMany(dimension => record.Scope[dimension].Select(value => new GovernanceScopeRow
                    {
                        OrganizationId = organizationId,
                        RecordId = record.Id,
                        Dimension = dimension,
                        ValueKey = GovernanceScopeRow.KeyOf(dimension, value),
                        Value = value,
                    }))
                    .DistinctBy(scope => (scope.Dimension, scope.ValueKey)));

                db.AddRange(record.Relations.Distinct().Select(relation => new GovernanceRelationRow
                {
                    OrganizationId = organizationId,
                    RecordId = record.Id,
                    Kind = relation.Kind,
                    TargetId = relation.TargetId,
                }));
            }
        }

        private async Task<SyncCheckpointRow> UpsertCheckpointAsync(
            GovernanceSourceConfig config,
            SourceCommit head,
            SyncOutcome outcome,
            ImmutableArray<ValidationIssue> issues,
            CancellationToken cancellationToken)
        {
            var row = await db.Set<SyncCheckpointRow>().SingleOrDefaultAsync(c => c.OrganizationId == organizationId, cancellationToken);
            var json = JsonSerializer.Serialize(issues.AsEnumerable(), IssueJson);
            if (row is null)
            {
                row = new SyncCheckpointRow
                {
                    OrganizationId = organizationId,
                    RepositoryUrl = config.RepositoryUrl,
                    Branch = config.Branch,
                    RootPath = config.RootPath,
                    HeadCommit = head.Sha,
                    Issues = json,
                };
                db.Add(row);
            }

            row.RepositoryUrl = config.RepositoryUrl;
            row.Branch = config.Branch;
            row.RootPath = config.RootPath;
            row.HeadCommit = head.Sha;
            row.HeadOutcome = outcome;
            row.Issues = json;
            row.UpdatedAt = timeProvider.GetUtcNow();
            return row;
        }

        /// <summary>Runs <paramref name="work"/> in one transaction on the session's connection and leaves the context empty.</summary>
        private Task InTransactionAsync(Func<Task> work, CancellationToken cancellationToken) =>
            db.Database.CreateExecutionStrategy().ExecuteAsync(
                async () =>
                {
                    db.ChangeTracker.Clear();
                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                    await work();
                    await transaction.CommitAsync(cancellationToken);
                    db.ChangeTracker.Clear();
                });
    }
}

/// <summary>
/// Stores integration events idempotently. Writers for one organization are serialized by a
/// transaction-level advisory lock, so two detectors running at once cannot both insert an event.
/// </summary>
public sealed class EfGovernanceEventWriter(DbContextOptions<AxiomDbContext> options) : IGovernanceEventWriter
{
    public async Task<int> WriteAsync(string organizationId, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentNullException.ThrowIfNull(events);
        if (events.Any(e => !string.Equals(e.OrganizationId, organizationId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Every event must belong to the organization it is written for.", nameof(events));
        }

        await using var db = new AxiomDbContext(options);
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var key = GovernanceLocks.Notification(organizationId);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);

            var outbox = new EfEventOutbox(db);
            foreach (var integrationEvent in events)
            {
                outbox.Enqueue(integrationEvent);
            }

            var written = await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return written;
        });
    }
}
