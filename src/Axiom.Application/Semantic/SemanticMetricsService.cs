using System.Collections.Immutable;
using Axiom.Application.Common;
using Microsoft.Extensions.Options;

namespace Axiom.Application.Semantic;

public sealed record SemanticMetricGroup(
    string Code,
    string Provider,
    string Model,
    int Raised,
    int Overturned,
    double OverturnRate,
    ImmutableArray<ConfidenceBand> Bands);

public sealed record SemanticMetricsReport(
    string OrganizationId,
    DateTimeOffset From,
    DateTimeOffset To,
    int Raised,
    int Overturned,
    double OverturnRate,
    ImmutableArray<SemanticMetricGroup> Groups,
    ImmutableArray<ConfidenceBand> Bands,
    ThresholdSuggestion Suggestion,
    bool Truncated);

/// <summary>
/// Overturn statistics of semantic findings (task 7.11). The overturn rate is the false-positive proxy: a
/// reviewer disagreed with the finding. Raised findings count by evaluation time, overturns by decision time.
/// </summary>
public sealed class SemanticMetricsService(
    ISemanticFeedbackStore feedback, IOptions<SemanticOptions> semantic, IOptions<SemanticCalibrationOptions> calibration, TimeProvider time)
{
    public const double BandWidth = 0.1;
    public const int ScanLimit = 20_000;
    public const int MaxWindowDays = 366;

    public async Task<SemanticMetricsReport> GetAsync(AxiomPrincipal principal, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.ReadAudit);

        var options = calibration.Value;
        var end = to ?? time.GetUtcNow();
        var start = from ?? end.AddDays(-Math.Clamp(options.DefaultWindowDays, 1, MaxWindowDays));
        if (start >= end)
        {
            throw AxiomException.Invalid("'from' must be earlier than 'to'.");
        }

        if (end - start > TimeSpan.FromDays(MaxWindowDays))
        {
            throw AxiomException.Invalid($"The window is limited to {MaxWindowDays} days.");
        }

        var raised = await feedback.ListRaisedAsync(principal.OrganizationId, start, end, ScanLimit, cancellationToken);
        var overturns = await feedback.ListOverturnsAsync(principal.OrganizationId, start, end, ScanLimit, cancellationToken);
        var report = Compute(principal.OrganizationId, start, end, raised, overturns, semantic.Value, options);

        foreach (var group in report.Groups)
        {
            AxiomTelemetry.SemanticOverturnRate.Record(group.OverturnRate,
                new KeyValuePair<string, object?>("code", group.Code),
                new KeyValuePair<string, object?>("provider", group.Provider),
                new KeyValuePair<string, object?>("model", group.Model));
        }

        return report;
    }

    public static SemanticMetricsReport Compute(
        string organizationId, DateTimeOffset from, DateTimeOffset to,
        IReadOnlyCollection<SemanticFindingObservation> raised, IReadOnlyCollection<SemanticFeedback> overturns,
        SemanticOptions semantic, SemanticCalibrationOptions calibration)
    {
        ArgumentNullException.ThrowIfNull(raised);
        ArgumentNullException.ThrowIfNull(overturns);
        ArgumentNullException.ThrowIfNull(semantic);
        ArgumentNullException.ThrowIfNull(calibration);

        var truncated = raised.Count >= ScanLimit || overturns.Count >= ScanLimit;
        var keys = raised.Select(r => (r.Code, r.Provider, r.Model))
            .Union(overturns.Select(o => (o.Code, o.Provider, o.Model)))
            .OrderBy(k => k.Code, StringComparer.Ordinal).ThenBy(k => k.Provider, StringComparer.Ordinal).ThenBy(k => k.Model, StringComparer.Ordinal);

        var groups = keys.Select(key =>
        {
            var r = raised.Where(x => (x.Code, x.Provider, x.Model) == key).Select(x => x.Confidence).ToList();
            var o = overturns.Where(x => (x.Code, x.Provider, x.Model) == key).Select(x => x.Confidence).ToList();
            return new SemanticMetricGroup(key.Code, key.Provider, key.Model, r.Count, o.Count, Rate(o.Count, r.Count), BandsOf(r, o));
        }).ToImmutableArray();

        var all = BandsOf([.. raised.Select(x => x.Confidence)], [.. overturns.Select(x => x.Confidence)]);
        var suggestion = ConfidenceCalibrator.Suggest(all, semantic.MinConfidence, semantic.ReviewConfidence, calibration);
        return new SemanticMetricsReport(organizationId, from, to, raised.Count, overturns.Count, Rate(overturns.Count, raised.Count), groups, all, suggestion, truncated);
    }

    private static double Rate(int overturned, int raised) => raised == 0 ? 0 : Math.Round(Math.Min(1, (double)overturned / raised), 4);

    private static ImmutableArray<ConfidenceBand> BandsOf(List<double> raised, List<double> overturned)
    {
        var count = (int)Math.Round(1 / BandWidth);
        var bands = new List<ConfidenceBand>();
        for (var i = 0; i < count; i++)
        {
            var lower = Math.Round(i * BandWidth, 2);
            var upper = Math.Round(lower + BandWidth, 2);
            var last = i == count - 1;
            var r = raised.Count(c => InBand(c, lower, upper, last));
            var o = overturned.Count(c => InBand(c, lower, upper, last));
            if (r > 0 || o > 0)
            {
                bands.Add(new ConfidenceBand(lower, upper, r, o));
            }
        }

        return [.. bands];
    }

    private static bool InBand(double confidence, double lower, double upper, bool last) =>
        confidence >= lower && (confidence < upper || (last && confidence <= upper));
}
