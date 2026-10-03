using System.Collections.Immutable;
using Axiom.Domain.Governance;

namespace Axiom.Application.Governance;

/// <summary>Where an organization's authoritative governance lives (ADR-0001).</summary>
public sealed record GovernanceSourceConfig(string OrganizationId, string RepositoryUrl, string Branch, string RootPath);

public sealed record SourceCommit(string Sha, DateTimeOffset CommittedAt, string Author);

public sealed record SourceFile(string Path, string BlobSha, string Content);

/// <summary>One commit in the history of a single governance file, oldest first.</summary>
public sealed record SourceFileVersion(SourceCommit Commit, string Path, string BlobSha, string Content);

/// <summary>Read access to the authoritative Git source. Implementations never write to the default branch.</summary>
public interface IGovernanceSource
{
    /// <summary>Fetches the remote and returns the head commit of the configured branch.</summary>
    Task<SourceCommit> FetchHeadAsync(GovernanceSourceConfig config, CancellationToken cancellationToken);

    /// <summary>All files under the configured root at <paramref name="commitSha"/>, with paths relative to the repository root.</summary>
    Task<IReadOnlyList<SourceFile>> ReadTreeAsync(GovernanceSourceConfig config, string commitSha, CancellationToken cancellationToken);

    /// <summary>Every distinct version of <paramref name="path"/> reachable from <paramref name="commitSha"/>, oldest first.</summary>
    Task<IReadOnlyList<SourceFileVersion>> ReadHistoryAsync(GovernanceSourceConfig config, string commitSha, string path, CancellationToken cancellationToken);

    /// <summary>Paths under the root that differ between two commits. Null when <paramref name="fromSha"/> is not an ancestor.</summary>
    Task<IReadOnlyList<string>?> ChangedPathsAsync(GovernanceSourceConfig config, string fromSha, string toSha, CancellationToken cancellationToken);
}

/// <summary>A published snapshot and how fresh it is relative to the source.</summary>
public sealed record SnapshotInfo(string SnapshotId, string OrganizationId, string SourceCommit, DateTimeOffset PublishedAt, int RecordCount);

/// <summary>Reads published governance snapshots. Snapshots are immutable, so implementations may cache by ID.</summary>
public interface IGovernanceSnapshotProvider
{
    Task<SnapshotInfo?> GetCurrentInfoAsync(string organizationId, CancellationToken cancellationToken);

    /// <summary>The currently published snapshot, or null when the organization has never synchronized.</summary>
    Task<GovernanceSnapshot?> GetCurrentAsync(string organizationId, CancellationToken cancellationToken);

    /// <summary>A specific, possibly historical, snapshot (receipt replay, P10).</summary>
    Task<GovernanceSnapshot?> GetAsync(string organizationId, string snapshotId, CancellationToken cancellationToken);
}

public enum SyncMode
{
    /// <summary>Apply only what changed since the last checkpoint; falls back to a rebuild when history diverged.</summary>
    Incremental,

    /// <summary>Discard the projection and rebuild it from the Git source (R25).</summary>
    Rebuild,
}

public enum SyncOutcome
{
    /// <summary>The projection already matched the source head.</summary>
    UpToDate,
    Published,

    /// <summary>The source contains errors; the previous snapshot stays in force (ADR-0008).</summary>
    Rejected,
}

public sealed record SyncResult(
    SyncOutcome Outcome,
    string SourceCommit,
    string? SnapshotId,
    int RecordCount,
    ImmutableArray<string> ChangedRecordIds,
    ImmutableArray<ValidationIssue> Issues);

/// <summary>Synchronizes the PostgreSQL projection with the authoritative Git source.</summary>
public interface IGovernanceSynchronizer
{
    Task<SyncResult> SynchronizeAsync(GovernanceSourceConfig config, SyncMode mode, CancellationToken cancellationToken);
}

/// <summary>Registered governance sources per organization.</summary>
public interface IGovernanceSourceRegistry
{
    Task<IReadOnlyList<GovernanceSourceConfig>> ListAsync(CancellationToken cancellationToken);

    Task<GovernanceSourceConfig?> FindAsync(string organizationId, CancellationToken cancellationToken);

    Task UpsertAsync(GovernanceSourceConfig config, CancellationToken cancellationToken);
}

/// <summary>Filters of R19: ID, text, scope, system, owner, status, technology, relationship.</summary>
public sealed record GovernanceQuery
{
    public string? Text { get; init; }

    public ImmutableArray<string> Ids { get; init; } = [];

    public ImmutableArray<RecordKind> Kinds { get; init; } = [];

    public ImmutableArray<LifecycleStatus> Statuses { get; init; } = [];

    public string? Owner { get; init; }

    public string? Tag { get; init; }

    /// <summary>Scope filters: a record matches when it restricts the dimension to the given value.</summary>
    public ImmutableDictionary<ScopeDimension, string> Scope { get; init; } = ImmutableDictionary<ScopeDimension, string>.Empty;

    /// <summary>Records that have a relation of this kind...</summary>
    public RelationKind? RelationKind { get; init; }

    /// <summary>...to this record ID.</summary>
    public string? RelatedTo { get; init; }

    /// <summary>Only records whose review date has passed.</summary>
    public bool StaleOnly { get; init; }

    public int Skip { get; init; }

    public int Take { get; init; } = 50;
}

public sealed record GovernanceSummary(
    string Id,
    RecordKind Kind,
    string Title,
    LifecycleStatus Status,
    bool IsAuthoritative,
    ImmutableArray<string> Owners,
    ImmutableArray<string> Tags,
    AuthorityLevel AuthorityLevel,
    bool Exemptable,
    int Revision,
    DateOnly? ReviewAfter,
    bool IsStale,
    string SourcePath,
    string SourceCommit);

public sealed record GovernanceSearchResult(ImmutableArray<GovernanceSummary> Items, int Total);

public sealed record RevisionInfo(int Revision, string ContentHash, LifecycleStatus Status, string CommitSha, DateTimeOffset CommittedAt, string Author, string Path);

public sealed record GovernanceDetail(RecordRevision Current, string SourceRepository, ImmutableArray<RevisionInfo> History, ImmutableArray<Relation> IncomingRelations);

/// <summary>Search and read access over the governance projection (task 1.12, R19).</summary>
public interface IGovernanceQueries
{
    Task<GovernanceSearchResult> SearchAsync(string organizationId, GovernanceQuery query, CancellationToken cancellationToken);

    Task<GovernanceDetail?> GetAsync(string organizationId, string recordId, CancellationToken cancellationToken);

    /// <summary>A specific historical revision of a record.</summary>
    Task<RecordRevision?> GetRevisionAsync(string organizationId, string recordId, int revision, CancellationToken cancellationToken);
}
