using System.Collections.Immutable;
using Axiom.Domain.Governance;

namespace Axiom.Application.Governance;

/// <summary><c>data</c> of <c>governance.record.changed</c> (docs/04-contracts/event-contracts.md).</summary>
/// <param name="RecordId">The record that changed.</param>
/// <param name="Kind">Record kind, lower case.</param>
/// <param name="ChangeType">added, modified, moved or removed.</param>
/// <param name="OldLifecycle">Lifecycle in the previously published snapshot; null when the record is new.</param>
/// <param name="NewLifecycle">Lifecycle after the change; null when the record was removed.</param>
/// <param name="Revision">Revision now in force; null when the record was removed.</param>
/// <param name="Repository">Git repository of the governance source.</param>
/// <param name="Commit">Commit that made the change.</param>
/// <param name="Path">Path of the record file; for a removal, the path it was removed from.</param>
/// <param name="Scope">Affected scope summary: restricted dimension to its values. Empty means unrestricted.</param>
public sealed record GovernanceRecordChangedData(
    string RecordId,
    string Kind,
    string ChangeType,
    string? OldLifecycle,
    string? NewLifecycle,
    int? Revision,
    string Repository,
    string Commit,
    string Path,
    ImmutableSortedDictionary<string, ImmutableArray<string>> Scope);

public sealed record SnapshotRecordCounts(int Total, int Added, int Modified, int Moved, int Removed);

/// <summary><c>data</c> of <c>governance.snapshot.published</c>.</summary>
public sealed record GovernanceSnapshotPublishedData(
    string SnapshotId,
    string SourceCommit,
    SnapshotRecordCounts RecordCounts,
    string ValidationStatus,
    int WarningCount);

/// <summary><c>data</c> of <c>governance.record.stale</c>.</summary>
/// <param name="RecordId">The stale record.</param>
/// <param name="ReviewDue">The review date that has passed.</param>
/// <param name="Owners">Accountable owners to notify.</param>
/// <param name="LastAppliedAt">When an evaluation last applied the record; null when no application is known.</param>
public sealed record GovernanceRecordStaleData(
    string RecordId,
    DateOnly ReviewDue,
    ImmutableArray<string> Owners,
    DateTimeOffset? LastAppliedAt);

internal static class GovernanceEventNames
{
    public const string Valid = "valid";
    public const string ValidWithWarnings = "valid_with_warnings";

    public static string Name(RecordKind kind) => kind.ToString().ToLowerInvariant();

    public static string Name(LifecycleStatus status) => status.ToString().ToLowerInvariant();

    public static string Name(RecordChangeType type) => type.ToString().ToLowerInvariant();

    public static ImmutableSortedDictionary<string, ImmutableArray<string>> Summarize(Scope scope) =>
        scope.RestrictedDimensions.ToImmutableSortedDictionary(
            dimension => char.ToLowerInvariant(dimension.ToString()[0]) + dimension.ToString()[1..],
            dimension => scope[dimension].ToImmutableArray(),
            StringComparer.Ordinal);
}
