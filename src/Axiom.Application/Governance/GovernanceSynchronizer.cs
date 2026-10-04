using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Domain.Governance;
using Microsoft.Extensions.Logging;

namespace Axiom.Application.Governance;

/// <summary>
/// Projects the authoritative Git source into the runtime store (ADR-0001) by replaying the
/// first-parent history of the governance branch one commit at a time.
/// <para>
/// Each commit is a pure step over (last published state, commit tree): the candidate record set is
/// the published set with the paths that differ from the last published commit re-read from Git.
/// A rebuild and an incremental run execute the same steps over the same commits, which is what
/// makes the projection reproducible (P2). A commit with any error is rejected as a whole and the
/// previous snapshot stays in force (ADR-0008).
/// </para>
/// </summary>
public sealed partial class GovernanceSynchronizer(
    IGovernanceSource source,
    IGovernanceCommitSource commits,
    IGovernanceRecordParser parser,
    IGovernanceProjectionStore store,
    TimeProvider timeProvider,
    ILogger<GovernanceSynchronizer> logger) : IGovernanceSynchronizer
{
    public const string UnreadableRecord = "GOV_UNREADABLE_RECORD";

    public async Task<SyncResult> SynchronizeAsync(GovernanceSourceConfig config, SyncMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.OrganizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.RepositoryUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Branch);
        if (GovernanceSourceUrl.HasEmbeddedCredentials(config.RepositoryUrl))
        {
            throw new ArgumentException("The governance repository URL must not embed credentials; supply them through the credentials port.", nameof(config));
        }

        // The source is contacted before anything is deleted, so an unreachable Git provider leaves
        // the last published snapshot readable (design.md §16).
        var head = await source.FetchHeadAsync(config, cancellationToken);

        await using var session = await store.OpenAsync(config.OrganizationId, cancellationToken);
        var checkpoint = await session.GetCheckpointAsync(cancellationToken);

        IReadOnlyList<SourceCommit>? pending = null;
        if (mode == SyncMode.Incremental && checkpoint is not null)
        {
            if (!IsSameSource(checkpoint, config))
            {
                LogSourceChanged(config.OrganizationId);
            }
            else if (string.Equals(checkpoint.HeadCommit, head.Sha, StringComparison.Ordinal))
            {
                return new SyncResult(
                    checkpoint.HeadOutcome == SyncOutcome.Rejected ? SyncOutcome.Rejected : SyncOutcome.UpToDate,
                    head.Sha,
                    checkpoint.SnapshotId,
                    checkpoint.RecordCount,
                    [],
                    checkpoint.Issues);
            }
            else
            {
                pending = await commits.ListFirstParentCommitsAsync(config, checkpoint.HeadCommit, head.Sha, cancellationToken);
                if (pending is null)
                {
                    LogHistoryRewritten(config.OrganizationId, checkpoint.HeadCommit, head.Sha);
                }
            }
        }

        if (pending is null)
        {
            pending = await commits.ListFirstParentCommitsAsync(config, null, head.Sha, cancellationToken)
                ?? throw new InvalidOperationException($"The history of commit '{head.Sha}' could not be read.");
            if (checkpoint is not null || mode == SyncMode.Rebuild)
            {
                await session.ResetAsync(cancellationToken);
                checkpoint = null;
            }
        }

        var walk = await LoadWalkAsync(session, checkpoint, cancellationToken);
        var changedIds = new SortedSet<string>(StringComparer.Ordinal);
        var outcome = SyncOutcome.UpToDate;
        ImmutableArray<ValidationIssue> issues = [];

        foreach (var commit in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = await ReadCandidateAsync(config, walk, commit, cancellationToken);
            issues = candidate.Issues;

            if (issues.Any(issue => issue.Severity == IssueSeverity.Error))
            {
                await session.RejectAsync(config, commit, issues, cancellationToken);
                outcome = SyncOutcome.Rejected;
                LogRejected(config.OrganizationId, commit.Sha, issues.Count(issue => issue.Severity == IssueSeverity.Error));
                continue;
            }

            var published = await BuildPublicationAsync(session, config, walk, commit, candidate, cancellationToken);
            await session.PublishAsync(config, published, cancellationToken);
            walk.Apply(published);
            changedIds.UnionWith(published.Changes.Where(change => change.Type != RecordChangeType.SourceRewritten).Select(change => change.RecordId));
            outcome = SyncOutcome.Published;
            LogPublished(config.OrganizationId, commit.Sha, published.SnapshotId, published.RecordCount);
        }

        return new SyncResult(outcome, head.Sha, walk.SnapshotId, walk.Records.Count, [.. changedIds], issues);
    }

    private static bool IsSameSource(SyncCheckpoint checkpoint, GovernanceSourceConfig config) =>
        string.Equals(checkpoint.RepositoryUrl, config.RepositoryUrl, StringComparison.Ordinal)
        && string.Equals(checkpoint.Branch, config.Branch, StringComparison.Ordinal)
        && string.Equals(GlobPattern.NormalizePath(checkpoint.RootPath), GlobPattern.NormalizePath(config.RootPath), StringComparison.Ordinal);

    private async Task<Walk> LoadWalkAsync(IGovernanceProjectionSession session, SyncCheckpoint? checkpoint, CancellationToken cancellationToken)
    {
        var walk = new Walk
        {
            PublishedCommit = checkpoint?.PublishedCommit,
            Sequence = checkpoint?.PublishedSequence ?? 0,
            SnapshotId = checkpoint?.SnapshotId,
        };
        if (checkpoint?.PublishedCommit is null)
        {
            return walk;
        }

        foreach (var state in await session.LoadPublishedRecordsAsync(cancellationToken))
        {
            var parsed = parser.Parse(state.Path, state.SourceText);
            if (parsed.Record is null || !string.Equals(parsed.Record.ContentHash, state.ContentHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Published revision {state.Revision} of record '{state.RecordId}' can no longer be read from its stored source. Rebuild the projection from Git.");
            }

            walk.Records[state.RecordId] = new Standing(parsed.Record, state.Revision, state.Path, state.BlobSha, state.CommitSha, state.SourceText);
        }

        return walk;
    }

    /// <summary>Forms and validates the full record set at <paramref name="commit"/>.</summary>
    private async Task<Candidate> ReadCandidateAsync(GovernanceSourceConfig config, Walk walk, SourceCommit commit, CancellationToken cancellationToken)
    {
        IReadOnlyList<SourceFile> files;
        HashSet<string> touched;
        if (walk.PublishedCommit is null)
        {
            // Nothing is published yet, so there is nothing to diff against: the whole tree is the candidate.
            files = [.. (await source.ReadTreeAsync(config, commit.Sha, cancellationToken)).Where(file => parser.IsRecordPath(file.Path))];
            touched = files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        }
        else
        {
            var changed = await source.ChangedPathsAsync(config, walk.PublishedCommit, commit.Sha, cancellationToken)
                ?? throw new InvalidOperationException($"Commit '{walk.PublishedCommit}' is not an ancestor of '{commit.Sha}' although both are on the first-parent chain.");
            touched = changed.Where(parser.IsRecordPath).ToHashSet(StringComparer.Ordinal);
            files = touched.Count == 0 ? [] : await commits.ReadFilesAsync(config, commit.Sha, touched, cancellationToken);
        }

        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();
        var standing = new List<Standing>();
        var parsedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
        {
            var parsed = parser.Parse(file.Path, file.Content);
            issues.AddRange(parsed.Issues);
            if (parsed.Record is null)
            {
                if (parsed.Issues.All(issue => issue.Severity != IssueSeverity.Error))
                {
                    issues.Add(ValidationIssue.Error(UnreadableRecord, "The file could not be read as a governance record.", sourcePath: file.Path));
                }

                continue;
            }

            // Revision 0 marks a record whose revision number is assigned at publication.
            standing.Add(new Standing(parsed.Record, 0, file.Path, file.BlobSha, commit.Sha, file.Content));
            parsedIds.Add(parsed.Record.Id);
        }

        var reread = standing.Count;
        standing.AddRange(walk.Records.Values.Where(record => !touched.Contains(record.Path)));

        issues.AddRange(GovernanceSetValidator.Validate([.. standing.Select(record => record.Record)]));

        // ADR-0008: lifecycle moves are judged against the last published revision, not the last commit.
        foreach (var record in standing.Take(reread).OrderBy(record => record.Record.Id, StringComparer.Ordinal))
        {
            if (walk.Records.TryGetValue(record.Record.Id, out var previous)
                && GovernanceSetValidator.ValidateTransition(previous.Record, record.Record) is { } illegal)
            {
                issues.Add(illegal with { SourcePath = record.Path });
            }
        }

        return new Candidate(standing, issues.ToImmutable());
    }

    private async Task<PublishedCommit> BuildPublicationAsync(
        IGovernanceProjectionSession session,
        GovernanceSourceConfig config,
        Walk walk,
        SourceCommit commit,
        Candidate candidate,
        CancellationToken cancellationToken)
    {
        // The set validator reported no duplicate IDs, so the candidate is keyed by record ID.
        var next = candidate.Records.ToDictionary(record => record.Record.Id, StringComparer.Ordinal);
        var needNumbers = next.Values
            .Where(record => !walk.Records.TryGetValue(record.Record.Id, out var previous)
                || !string.Equals(previous.Record.ContentHash, record.Record.ContentHash, StringComparison.Ordinal))
            .Select(record => record.Record.Id)
            .ToArray();
        var known = needNumbers.Length == 0
            ? ImmutableDictionary<string, IReadOnlyDictionary<string, int>>.Empty
            : await session.LoadRevisionNumbersAsync(needNumbers, cancellationToken);

        var changes = ImmutableArray.CreateBuilder<RecordChange>();
        var events = ImmutableArray.CreateBuilder<IntegrationEvent>();

        foreach (var id in walk.Records.Keys.Union(next.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            walk.Records.TryGetValue(id, out var before);
            next.TryGetValue(id, out var after);

            RecordChange change;
            if (after is null)
            {
                change = new RecordChange(id, RecordChangeType.Removed, null, null, false, null, null, null);
            }
            else if (before is null || !string.Equals(before.Record.ContentHash, after.Record.ContentHash, StringComparison.Ordinal))
            {
                // Revision N is the Nth distinct content of this record ID; content seen before keeps its number.
                var numbers = known.TryGetValue(id, out var assigned) ? assigned : ImmutableDictionary<string, int>.Empty;
                var isNew = !numbers.TryGetValue(after.Record.ContentHash, out var revision);
                if (isNew)
                {
                    revision = numbers.Count == 0 ? 1 : numbers.Values.Max() + 1;
                }

                change = new RecordChange(id, before is null ? RecordChangeType.Added : RecordChangeType.Modified, after.Record, revision, isNew, after.Path, after.BlobSha, after.SourceText);
            }
            else if (!string.Equals(before.Path, after.Path, StringComparison.Ordinal))
            {
                change = new RecordChange(id, RecordChangeType.Moved, after.Record, before.Revision, false, after.Path, after.BlobSha, after.SourceText);
            }
            else if (!string.Equals(before.BlobSha, after.BlobSha, StringComparison.Ordinal))
            {
                change = new RecordChange(id, RecordChangeType.SourceRewritten, after.Record, before.Revision, false, after.Path, after.BlobSha, after.SourceText);
            }
            else
            {
                continue;
            }

            changes.Add(change);
            if (change.Type != RecordChangeType.SourceRewritten)
            {
                var described = after?.Record ?? before!.Record;
                events.Add(IntegrationEvent.Create(
                    EventTypes.GovernanceRecordChanged,
                    config.OrganizationId,
                    commit.CommittedAt,
                    $"{commit.Sha}|{id}",
                    new GovernanceRecordChangedData(
                        id,
                        GovernanceEventNames.Name(described.Kind),
                        GovernanceEventNames.Name(change.Type),
                        before is null ? null : GovernanceEventNames.Name(before.Record.Status),
                        after is null ? null : GovernanceEventNames.Name(after.Record.Status),
                        change.Revision,
                        config.RepositoryUrl,
                        commit.Sha,
                        after?.Path ?? before!.Path,
                        GovernanceEventNames.Summarize(described.Scope))));
            }
        }

        var snapshotId = GovernanceSnapshot.ComputeId(config.OrganizationId, next.Values.Select(record => (record.Record.Id, record.Record.ContentHash)));
        var publishedAt = timeProvider.GetUtcNow();
        var warnings = candidate.Issues;
        int Count(RecordChangeType type) => changes.Count(change => change.Type == type);

        events.Add(IntegrationEvent.Create(
            EventTypes.GovernanceSnapshotPublished,
            config.OrganizationId,
            publishedAt,
            $"{commit.Sha}|{snapshotId}",
            new GovernanceSnapshotPublishedData(
                snapshotId,
                commit.Sha,
                new SnapshotRecordCounts(next.Count, Count(RecordChangeType.Added), Count(RecordChangeType.Modified), Count(RecordChangeType.Moved), Count(RecordChangeType.Removed)),
                warnings.IsEmpty ? GovernanceEventNames.Valid : GovernanceEventNames.ValidWithWarnings,
                warnings.Length)));

        return new PublishedCommit(commit, walk.Sequence + 1, snapshotId, next.Count, publishedAt, warnings, changes.ToImmutable(), events.ToImmutable());
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Governance commit {CommitSha} of organization {OrganizationId} published as snapshot {SnapshotId} with {RecordCount} records.")]
    private partial void LogPublished(string organizationId, string commitSha, string snapshotId, int recordCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Governance commit {CommitSha} of organization {OrganizationId} rejected with {ErrorCount} errors; the previous snapshot stays in force.")]
    private partial void LogRejected(string organizationId, string commitSha, int errorCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Governance history of organization {OrganizationId} no longer contains checkpoint {CheckpointSha} on the first-parent chain of {HeadSha}; rebuilding the projection.")]
    private partial void LogHistoryRewritten(string organizationId, string checkpointSha, string headSha);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Governance source of organization {OrganizationId} changed since the last synchronization; rebuilding the projection.")]
    private partial void LogSourceChanged(string organizationId);

    /// <summary>A record as it stands at one point of the walk.</summary>
    private sealed record Standing(GovernanceRecord Record, int Revision, string Path, string BlobSha, string CommitSha, string SourceText);

    private sealed record Candidate(List<Standing> Records, ImmutableArray<ValidationIssue> Issues);

    /// <summary>The last published state, which is all a step of the walk depends on besides the commit itself.</summary>
    private sealed class Walk
    {
        public Dictionary<string, Standing> Records { get; } = new(StringComparer.Ordinal);

        public string? PublishedCommit { get; set; }

        public int Sequence { get; set; }

        public string? SnapshotId { get; set; }

        public void Apply(PublishedCommit published)
        {
            foreach (var change in published.Changes)
            {
                if (change.Type == RecordChangeType.Removed)
                {
                    Records.Remove(change.RecordId);
                }
                else
                {
                    Records[change.RecordId] = new Standing(change.Record!, change.Revision!.Value, change.Path!, change.BlobSha!, published.Commit.Sha, change.SourceText!);
                }
            }

            PublishedCommit = published.Commit.Sha;
            Sequence = published.Sequence;
            SnapshotId = published.SnapshotId;
        }
    }
}

/// <summary>Checks on governance repository locations.</summary>
public static class GovernanceSourceUrl
{
    /// <summary>True for URLs of the form <c>scheme://user:secret@host/…</c>, which would persist a secret with the source.</summary>
    public static bool HasEmbeddedCredentials(string repositoryUrl)
    {
        ArgumentNullException.ThrowIfNull(repositoryUrl);
        var scheme = repositoryUrl.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
        {
            return false;
        }

        var authorityEnd = repositoryUrl.IndexOf('/', scheme + 3);
        var authority = authorityEnd < 0 ? repositoryUrl[(scheme + 3)..] : repositoryUrl[(scheme + 3)..authorityEnd];
        return authority.Contains('@', StringComparison.Ordinal);
    }
}
