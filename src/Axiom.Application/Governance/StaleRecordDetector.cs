using System.Collections.Immutable;
using System.Globalization;
using Axiom.Application.Common;

namespace Axiom.Application.Governance;

/// <param name="StaleRecordIds">Every accepted record whose review date has passed.</param>
/// <param name="NewlyFlagged">How many staleness events this run queued; zero when all were flagged before.</param>
public sealed record StaleDetectionResult(ImmutableArray<string> StaleRecordIds, int NewlyFlagged);

/// <summary>
/// Flags accepted records whose review date has passed (R21). Detection only notifies: the record
/// keeps governing until its owners change it in Git. One event is queued per record and review
/// date, so running the detector again does not notify again until the review date moves.
/// </summary>
public sealed class StaleRecordDetector(IGovernanceQueries queries, IGovernanceEventWriter events, TimeProvider timeProvider)
{
    private const int PageSize = 200;

    public async Task<StaleDetectionResult> DetectAsync(string organizationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        var now = timeProvider.GetUtcNow();
        var stale = new List<GovernanceSummary>();
        for (var skip = 0; ; skip += PageSize)
        {
            var page = await queries.SearchAsync(organizationId, new GovernanceQuery { StaleOnly = true, Skip = skip, Take = PageSize }, cancellationToken);
            stale.AddRange(page.Items);
            if (page.Items.Length < PageSize)
            {
                break;
            }
        }

        var notices = stale
            .Where(record => record.ReviewAfter is not null)
            .DistinctBy(record => record.Id, StringComparer.Ordinal)
            .Select(record => IntegrationEvent.Create(
                EventTypes.GovernanceRecordStale,
                organizationId,
                now,
                $"{record.Id}|{record.ReviewAfter!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
                new GovernanceRecordStaleData(record.Id, record.ReviewAfter.Value, record.Owners, LastAppliedAt: null)))
            .ToList();

        var queued = notices.Count == 0 ? 0 : await events.WriteAsync(organizationId, notices, cancellationToken);
        return new StaleDetectionResult([.. stale.Select(record => record.Id).Distinct(StringComparer.Ordinal)], queued);
    }
}
