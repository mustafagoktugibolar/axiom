using Axiom.Application.Governance;
using Axiom.Domain.Governance;
using NpgsqlTypes;

namespace Axiom.Infrastructure.Governance.Persistence;

// Projection of the authoritative Git source (ADR-0001). Every table is keyed by organization first
// (R23) and, except governance_source, is deleted and rebuilt from Git on a rebuild (R25).

/// <summary><c>governance_source</c>: where an organization's governance lives. Configuration, not projection.</summary>
public sealed class GovernanceSourceRow
{
    public required string OrganizationId { get; init; }

    public required string RepositoryUrl { get; set; }

    public required string Branch { get; set; }

    public required string RootPath { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary><c>governance_record</c>: one row per record of the currently published snapshot.</summary>
public sealed class GovernanceRecordRow
{
    public required string OrganizationId { get; init; }

    public required string RecordId { get; init; }

    public RecordKind Kind { get; set; }

    public LifecycleStatus Status { get; set; }

    public required string Title { get; set; }

    public required string Statement { get; set; }

    public required List<string> Owners { get; set; }

    public required List<string> Tags { get; set; }

    /// <summary>Tags as one string, because a generated tsvector column cannot read an array.</summary>
    public required string TagsText { get; set; }

    public DateOnly? ReviewAfter { get; set; }

    /// <summary>The revision in force; its source text is in <c>governance_revision</c>.</summary>
    public int Revision { get; set; }

    public required string ContentHash { get; set; }

    public required string Path { get; set; }

    public required string BlobSha { get; set; }

    /// <summary>The commit that put the record in its current state.</summary>
    public required string CommitSha { get; set; }

    public DateTimeOffset CommittedAt { get; set; }

    public required string Author { get; set; }

    /// <summary>Generated from ID, title, statement and tags; never written by the application.</summary>
    public NpgsqlTsVector SearchVector { get; private set; } = null!;
}

/// <summary>
/// <c>governance_revision</c>: revision N of a record is the Nth distinct content of that record ID
/// along the published history. Rows are never updated; the raw source text is kept so any revision
/// can be re-parsed. Coordinates are those of the commit that first published the content.
/// </summary>
public sealed class GovernanceRevisionRow
{
    public required string OrganizationId { get; init; }

    public required string RecordId { get; init; }

    public required int Revision { get; init; }

    public required string ContentHash { get; init; }

    public required RecordKind Kind { get; init; }

    public required LifecycleStatus Status { get; init; }

    public required string Title { get; init; }

    public required string Path { get; init; }

    public required string BlobSha { get; init; }

    public required string CommitSha { get; init; }

    public required DateTimeOffset CommittedAt { get; init; }

    public required string Author { get; init; }

    public required string SourceText { get; init; }
}

/// <summary><c>governance_scope</c>: one row per scope value of a current record.</summary>
public sealed class GovernanceScopeRow
{
    public required string OrganizationId { get; init; }

    public required string RecordId { get; init; }

    public required ScopeDimension Dimension { get; init; }

    /// <summary>The value in the form scope values are compared in: as written for paths, lower case otherwise.</summary>
    public required string ValueKey { get; init; }

    public required string Value { get; init; }

    public static string KeyOf(ScopeDimension dimension, string value) =>
        dimension == ScopeDimension.Path ? GlobPattern.NormalizePath(value) : value.Trim().ToLowerInvariant();
}

/// <summary><c>governance_relation</c>: one row per relation declared by a current record.</summary>
public sealed class GovernanceRelationRow
{
    public required string OrganizationId { get; init; }

    public required string RecordId { get; init; }

    public required RelationKind Kind { get; init; }

    public required string TargetId { get; init; }
}

/// <summary>
/// <c>governance_snapshot</c>: one row per published commit. The snapshot ID is content-addressed, so
/// commits that leave the records untouched share it; the commit is what identifies the row.
/// </summary>
public sealed class GovernanceSnapshotRow
{
    public required string OrganizationId { get; init; }

    public required string SourceCommit { get; init; }

    /// <summary>1-based ordinal among the organization's published commits.</summary>
    public required int Sequence { get; init; }

    public required string SnapshotId { get; init; }

    public required int RecordCount { get; init; }

    public required int WarningCount { get; init; }

    public required DateTimeOffset CommittedAt { get; init; }

    public required string Author { get; init; }

    public required DateTimeOffset PublishedAt { get; init; }
}

/// <summary>
/// <c>governance_snapshot_entry</c>: the membership log of snapshots. A row says that from snapshot
/// <see cref="FromSequence"/> on, the record stands at <see cref="Revision"/> (null: removed) until
/// a later row for the same record. The membership of snapshot N is each record's latest row with
/// <c>from_sequence &lt;= N</c>, which keeps every historical snapshot reconstructible (P10)
/// without storing one row per record per commit.
/// </summary>
public sealed class GovernanceSnapshotEntryRow
{
    public required string OrganizationId { get; init; }

    public required string RecordId { get; init; }

    public required int FromSequence { get; init; }

    public required int? Revision { get; init; }

    public required string? Path { get; init; }

    public required string? BlobSha { get; init; }

    public required string CommitSha { get; init; }
}

/// <summary><c>sync_checkpoint</c>: how far the walk over the source history has come.</summary>
public sealed class SyncCheckpointRow
{
    public required string OrganizationId { get; init; }

    public required string RepositoryUrl { get; set; }

    public required string Branch { get; set; }

    public required string RootPath { get; set; }

    public required string HeadCommit { get; set; }

    public SyncOutcome HeadOutcome { get; set; }

    public string? PublishedCommit { get; set; }

    public int PublishedSequence { get; set; }

    public string? SnapshotId { get; set; }

    public int RecordCount { get; set; }

    /// <summary>Validation issues of the head commit as JSON.</summary>
    public required string Issues { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
