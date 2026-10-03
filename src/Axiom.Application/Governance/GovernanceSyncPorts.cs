using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Domain.Governance;

namespace Axiom.Application.Governance;

/// <summary>
/// Commit-level reads the synchronizer needs in addition to <see cref="IGovernanceSource"/>. Both are
/// answered from the mirror populated by <see cref="IGovernanceSource.FetchHeadAsync"/>.
/// </summary>
public interface IGovernanceCommitSource
{
    /// <summary>
    /// The first-parent chain ending at <paramref name="headSha"/>, oldest first. With
    /// <paramref name="afterSha"/> the chain starts after that commit; null is returned when
    /// <paramref name="afterSha"/> is not on the first-parent chain (history was rewritten or re-parented).
    /// </summary>
    Task<IReadOnlyList<SourceCommit>?> ListFirstParentCommitsAsync(GovernanceSourceConfig config, string? afterSha, string headSha, CancellationToken cancellationToken);

    /// <summary>The requested paths as they exist at <paramref name="commitSha"/>; paths absent from that commit are omitted.</summary>
    Task<IReadOnlyList<SourceFile>> ReadFilesAsync(GovernanceSourceConfig config, string commitSha, IReadOnlyCollection<string> paths, CancellationToken cancellationToken);
}

/// <summary>A secret used to read a governance repository. Never logged and never persisted.</summary>
public sealed record GovernanceSourceCredential(string Username, string Secret)
{
    public override string ToString() => $"{nameof(GovernanceSourceCredential)} {{ Username = {Username}, Secret = *** }}";
}

/// <summary>Supplies the credential for a governance source, or null when the source is read anonymously.</summary>
public interface IGovernanceSourceCredentials
{
    ValueTask<GovernanceSourceCredential?> ResolveAsync(GovernanceSourceConfig config, CancellationToken cancellationToken);
}

/// <summary>Where the walk over the source history stands for one organization.</summary>
/// <param name="RepositoryUrl">Repository the projection was built from.</param>
/// <param name="Branch">Branch the projection was built from.</param>
/// <param name="RootPath">Governance root the projection was built from.</param>
/// <param name="HeadCommit">The last commit that was processed, published or not.</param>
/// <param name="HeadOutcome">Whether <paramref name="HeadCommit"/> was published or rejected.</param>
/// <param name="PublishedCommit">The last commit that was published; null until the first publication.</param>
/// <param name="PublishedSequence">How many commits have been published; the ordinal of the current snapshot.</param>
/// <param name="SnapshotId">The current snapshot; null until the first publication.</param>
/// <param name="RecordCount">Records in the current snapshot.</param>
/// <param name="Issues">Issues of <paramref name="HeadCommit"/>: the errors that rejected it, or the warnings it was published with.</param>
public sealed record SyncCheckpoint(
    string RepositoryUrl,
    string Branch,
    string RootPath,
    string HeadCommit,
    SyncOutcome HeadOutcome,
    string? PublishedCommit,
    int PublishedSequence,
    string? SnapshotId,
    int RecordCount,
    ImmutableArray<ValidationIssue> Issues);

/// <summary>A record as it stands in the currently published snapshot.</summary>
public sealed record PublishedRecordState(string RecordId, int Revision, string ContentHash, string Path, string BlobSha, string CommitSha, string SourceText);

public enum RecordChangeType
{
    Added,
    Modified,

    /// <summary>Same content at a different path.</summary>
    Moved,
    Removed,

    /// <summary>Same canonical content and path but a different Git blob (for example line endings).</summary>
    SourceRewritten,
}

/// <summary>What a published commit did to one record. <see cref="Record"/> and the coordinates are null for a removal.</summary>
public sealed record RecordChange(
    string RecordId,
    RecordChangeType Type,
    GovernanceRecord? Record,
    int? Revision,
    bool IsNewRevision,
    string? Path,
    string? BlobSha,
    string? SourceText);

/// <summary>Everything one published commit writes to the projection, applied atomically with its events.</summary>
public sealed record PublishedCommit(
    SourceCommit Commit,
    int Sequence,
    string SnapshotId,
    int RecordCount,
    DateTimeOffset PublishedAt,
    ImmutableArray<ValidationIssue> Warnings,
    ImmutableArray<RecordChange> Changes,
    ImmutableArray<IntegrationEvent> Events);

/// <summary>Opens exclusive write sessions on an organization's governance projection.</summary>
public interface IGovernanceProjectionStore
{
    /// <summary>
    /// Waits until no other synchronizer holds the organization's projection, then returns a session
    /// that holds it until disposed.
    /// </summary>
    Task<IGovernanceProjectionSession> OpenAsync(string organizationId, CancellationToken cancellationToken);
}

/// <summary>Exclusive write access to one organization's governance projection.</summary>
public interface IGovernanceProjectionSession : IAsyncDisposable
{
    Task<SyncCheckpoint?> GetCheckpointAsync(CancellationToken cancellationToken);

    /// <summary>The records of the currently published snapshot with the source text of their revision.</summary>
    Task<IReadOnlyList<PublishedRecordState>> LoadPublishedRecordsAsync(CancellationToken cancellationToken);

    /// <summary>For each requested record ID, the revision number already assigned to each content hash.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>> LoadRevisionNumbersAsync(IReadOnlyCollection<string> recordIds, CancellationToken cancellationToken);

    /// <summary>Deletes the organization's governance projection, including its checkpoint.</summary>
    Task ResetAsync(CancellationToken cancellationToken);

    /// <summary>Applies a published commit, its events and the checkpoint in one transaction.</summary>
    Task PublishAsync(GovernanceSourceConfig config, PublishedCommit commit, CancellationToken cancellationToken);

    /// <summary>Records a rejected commit on the checkpoint. The published snapshot is left untouched (ADR-0008).</summary>
    Task RejectAsync(GovernanceSourceConfig config, SourceCommit commit, ImmutableArray<ValidationIssue> issues, CancellationToken cancellationToken);
}

/// <summary>Stores integration events that are not tied to a projection change (for example staleness notices).</summary>
public interface IGovernanceEventWriter
{
    /// <summary>Stores the events that are not already stored and returns how many were new.</summary>
    Task<int> WriteAsync(string organizationId, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken);
}
