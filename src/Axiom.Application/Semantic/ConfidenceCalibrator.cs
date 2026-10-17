namespace Axiom.Application.Semantic;

/// <summary>Calibration inputs, bound from <c>Axiom:Semantic:Calibration</c>.</summary>
public sealed class SemanticCalibrationOptions
{
    public const string SectionName = "Axiom:Semantic:Calibration";

    /// <summary>The overturn rate the kept findings (confidence at or above MinConfidence) should stay under.</summary>
    public double TargetOverturnRate { get; set; } = 0.25;

    /// <summary>The overturn rate the findings that ask for review (at or above ReviewConfidence) should stay under.</summary>
    public double ReviewTargetOverturnRate { get; set; } = 0.10;

    /// <summary>Findings needed above a threshold before it is suggested; below this the data is noise.</summary>
    public int MinSamples { get; set; } = 20;

    public int DefaultWindowDays { get; set; } = 30;
}

public sealed record ConfidenceBand(double Lower, double Upper, int Raised, int Overturned)
{
    public double OverturnRate => Raised == 0 ? 0 : Math.Round(Math.Min(1, (double)Overturned / Raised), 4);
}

/// <summary>A suggestion only. Nothing applies it: enforcement thresholds change by configuration and review (ADR-0005).</summary>
public sealed record ThresholdSuggestion(
    bool HasSuggestion,
    double? MinConfidence,
    double? ReviewConfidence,
    double CurrentMinConfidence,
    double CurrentReviewConfidence,
    string Note);

/// <summary>
/// Pure threshold suggestion (task 7.9): the lowest confidence at or above which the observed overturn rate
/// of the findings kept stays under the target. Only confidences at or above the current MinConfidence were
/// ever observed, so a suggestion never lowers MinConfidence below a value that has data behind it.
/// </summary>
public static class ConfidenceCalibrator
{
    public static ThresholdSuggestion Suggest(
        IReadOnlyCollection<ConfidenceBand> bands, double currentMin, double currentReview, SemanticCalibrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(bands);
        ArgumentNullException.ThrowIfNull(options);

        var ordered = bands.Where(b => b.Raised > 0).OrderBy(b => b.Lower).ToList();
        var total = ordered.Sum(b => b.Raised);
        if (total < options.MinSamples)
        {
            return new ThresholdSuggestion(false, null, null, currentMin, currentReview,
                $"Not enough data: {total} findings observed, {options.MinSamples} needed.");
        }

        var min = LowestMeeting(ordered, options.TargetOverturnRate, options.MinSamples);
        if (min is null)
        {
            return new ThresholdSuggestion(false, null, null, currentMin, currentReview,
                "No confidence threshold keeps the overturn rate under the target with enough findings; review the provider, model or prompt instead.");
        }

        var review = LowestMeeting([.. ordered.Where(b => b.Lower >= min.Value)], options.ReviewTargetOverturnRate, options.MinSamples);
        var reviewValue = Math.Max(review ?? 1.0, min.Value);
        var note = review is null
            ? "No threshold keeps review-level findings under the review target; ReviewConfidence is suggested at 1.0 so nothing asks for review."
            : "Suggested thresholds would have kept the observed overturn rate under the targets.";
        return new ThresholdSuggestion(true, Math.Round(min.Value, 2), Math.Round(reviewValue, 2), currentMin, currentReview, note);
    }

    /// <summary>Lowest band edge whose cumulative (edge and above) overturn rate meets the target with enough samples.</summary>
    private static double? LowestMeeting(List<ConfidenceBand> ordered, double target, int minSamples)
    {
        double? best = null;
        for (var i = ordered.Count - 1; i >= 0; i--)
        {
            var raised = 0;
            var overturned = 0;
            for (var j = i; j < ordered.Count; j++)
            {
                raised += ordered[j].Raised;
                overturned += ordered[j].Overturned;
            }

            if (raised >= minSamples && (double)overturned / raised <= target)
            {
                best = ordered[i].Lower;
            }
        }

        return best;
    }
}
