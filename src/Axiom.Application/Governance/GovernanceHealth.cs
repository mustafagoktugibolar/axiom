using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Domain.Governance;
using Microsoft.Extensions.Options;

namespace Axiom.Application.Governance;

/// <summary>Governance health settings, bound from <c>Axiom:Governance:Health</c>.</summary>
public sealed class GovernanceHealthOptions
{
    public const string SectionName = "Axiom:Governance:Health";

    /// <summary>An accepted exception counts as expiring, and is announced, when it expires within this window.</summary>
    public TimeSpan ExpiryWindow { get; set; } = TimeSpan.FromDays(14);
}

/// <summary>A record that needs attention, with the date that makes it so (review due, expiry) when there is one.</summary>
public sealed record GovernanceHealthItem(string RecordId, string Kind, string Title, ImmutableArray<string> Owners, DateTimeOffset? DueAt, ImmutableArray<string> ReferencedBy);

/// <summary>Everything the health computation found in one snapshot. The lists are complete; callers bound them for display.</summary>
public sealed record GovernanceHealthReport(
    string SnapshotId,
    DateTimeOffset EvaluatedAt,
    ImmutableArray<GovernanceHealthItem> StaleRecords,
    ImmutableArray<GovernanceHealthItem> RecordsWithoutOwners,
    ImmutableArray<GovernanceHealthItem> DeprecatedStillReferenced,
    ImmutableArray<GovernanceHealthItem> ExpiringExceptions);

/// <summary>
/// Pure computation of governance health from a published snapshot (R21, R10): records past their
/// review date, records nobody owns, deprecated records that live records still depend on, and
/// accepted exceptions about to expire.
/// </summary>
public static class GovernanceHealthCalculator
{
    /// <summary>Relations that make a record depend on its target; supersession, conflict and "related" do not.</summary>
    private static readonly ImmutableHashSet<RelationKind> DependencyRelations =
        [RelationKind.Requires, RelationKind.Refines, RelationKind.Implements, RelationKind.MotivatedBy, RelationKind.ExceptionTo];

    public static GovernanceHealthReport Compute(GovernanceSnapshot snapshot, DateTimeOffset now, TimeSpan expiryWindow)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var records = snapshot.Revisions.Select(r => r.Record).ToList();

        var stale = records
            .Where(r => r.Status == LifecycleStatus.Accepted && r.Validity.IsReviewOverdueOn(today))
            .Select(r => Item(r, new DateTimeOffset(r.Validity.ReviewAfter!.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)))
            .ToImmutableArray();

        var ownerless = records
            .Where(r => r.Owners.IsDefaultOrEmpty || r.Owners.All(string.IsNullOrWhiteSpace))
            .Select(r => Item(r, null))
            .ToImmutableArray();

        // Only live records count as users of a deprecated one; a deprecated record pointing at another is already on its way out.
        var users = records
            .Where(r => r.Status is LifecycleStatus.Accepted or LifecycleStatus.Proposed)
            .SelectMany(r => r.Relations.Where(x => DependencyRelations.Contains(x.Kind)).Select(x => (Target: x.TargetId, User: r.Id)))
            .ToLookup(x => x.Target, x => x.User, StringComparer.Ordinal);
        var deprecated = records
            .Where(r => r.Status is LifecycleStatus.Deprecated or LifecycleStatus.Superseded && users.Contains(r.Id))
            .Select(r => Item(r, null) with { ReferencedBy = [.. users[r.Id].Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)] })
            .ToImmutableArray();

        var expiring = records
            .Where(r => r.Kind == RecordKind.Exception && r.Status == LifecycleStatus.Accepted && r.Exception is { } terms && terms.ExpiresAt > now && terms.ExpiresAt <= now + expiryWindow)
            .OrderBy(r => r.Exception!.ExpiresAt)
            .Select(r => Item(r, r.Exception!.ExpiresAt))
            .ToImmutableArray();

        return new GovernanceHealthReport(snapshot.Id, now, ById(stale), ById(ownerless), ById(deprecated), expiring);
    }

    private static ImmutableArray<GovernanceHealthItem> ById(ImmutableArray<GovernanceHealthItem> items) =>
        [.. items.OrderBy(i => i.RecordId, StringComparer.Ordinal)];

    private static GovernanceHealthItem Item(GovernanceRecord record, DateTimeOffset? dueAt) =>
        new(record.Id, record.Kind.ToString(), record.Title, record.Owners.IsDefault ? [] : record.Owners, dueAt, []);
}

public sealed record GovernanceHealthCounts(int StaleRecords, int RecordsWithoutOwners, int DeprecatedStillReferenced, int ExpiringExceptions);

/// <summary>The summary REST returns: counts are exact, each list is one bounded page.</summary>
public sealed record GovernanceHealthDto(
    string? SnapshotId,
    DateTimeOffset EvaluatedAt,
    int ExpiryWindowDays,
    GovernanceHealthCounts Counts,
    ImmutableArray<GovernanceHealthItem> StaleRecords,
    ImmutableArray<GovernanceHealthItem> RecordsWithoutOwners,
    ImmutableArray<GovernanceHealthItem> DeprecatedStillReferenced,
    ImmutableArray<GovernanceHealthItem> ExpiringExceptions);

/// <summary>Read access to the governance health summary for REST.</summary>
public sealed class GovernanceHealthService(IGovernanceSnapshotProvider snapshots, IOptions<GovernanceHealthOptions> options, TimeProvider time)
{
    public const int MaxPageSize = 200;

    public async Task<GovernanceHealthDto> GetAsync(AxiomPrincipal principal, int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.ReadGovernance);
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, MaxPageSize);

        var now = time.GetUtcNow();
        var window = options.Value.ExpiryWindow;
        var snapshot = await snapshots.GetCurrentAsync(principal.OrganizationId, cancellationToken);
        if (snapshot is null)
        {
            return new GovernanceHealthDto(null, now, (int)window.TotalDays, new GovernanceHealthCounts(0, 0, 0, 0), [], [], [], []);
        }

        var report = GovernanceHealthCalculator.Compute(snapshot, now, window);
        ImmutableArray<GovernanceHealthItem> Page(ImmutableArray<GovernanceHealthItem> items) => [.. items.Skip(skip).Take(take)];
        return new GovernanceHealthDto(
            report.SnapshotId,
            now,
            (int)window.TotalDays,
            new GovernanceHealthCounts(report.StaleRecords.Length, report.RecordsWithoutOwners.Length, report.DeprecatedStillReferenced.Length, report.ExpiringExceptions.Length),
            Page(report.StaleRecords),
            Page(report.RecordsWithoutOwners),
            Page(report.DeprecatedStillReferenced),
            Page(report.ExpiringExceptions));
    }
}

/// <summary><c>data</c> of <c>exception.expiring</c> (docs/04-contracts/event-contracts.md).</summary>
/// <param name="ExceptionId">The exception record about to expire.</param>
/// <param name="Targets">Decisions the exception waives.</param>
/// <param name="ExpiresAt">When the waiver ends.</param>
/// <param name="Owners">Owners to notify.</param>
/// <param name="ObservedRecentUsage">How often the exception was applied recently; null because applications are not aggregated yet.</param>
public sealed record ExceptionExpiringData(string ExceptionId, ImmutableArray<string> Targets, DateTimeOffset ExpiresAt, ImmutableArray<string> Owners, int? ObservedRecentUsage);

/// <summary>
/// Queues <c>exception.expiring</c> for every accepted exception that expires within the configured
/// window. The event ID derives from the exception and its expiry, so repeated runs notify once per
/// exception until an amendment moves the expiry.
/// </summary>
public sealed class ExceptionExpiryNotifier(IGovernanceSnapshotProvider snapshots, IGovernanceEventWriter events, IOptions<GovernanceHealthOptions> options, TimeProvider time)
{
    /// <returns>How many notices were new.</returns>
    public async Task<int> NotifyAsync(string organizationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        var snapshot = await snapshots.GetCurrentAsync(organizationId, cancellationToken);
        if (snapshot is null)
        {
            return 0;
        }

        var now = time.GetUtcNow();
        var report = GovernanceHealthCalculator.Compute(snapshot, now, options.Value.ExpiryWindow);
        var notices = new List<IntegrationEvent>();
        foreach (var item in report.ExpiringExceptions)
        {
            var terms = snapshot.Find(item.RecordId)!.Record.Exception!;
            notices.Add(IntegrationEvent.Create(
                EventTypes.ExceptionExpiring,
                organizationId,
                now,
                $"{item.RecordId}|{terms.ExpiresAt.UtcDateTime:O}",
                new ExceptionExpiringData(item.RecordId, terms.Targets, terms.ExpiresAt, item.Owners, ObservedRecentUsage: null)));
        }

        return notices.Count == 0 ? 0 : await events.WriteAsync(organizationId, notices, cancellationToken);
    }
}
