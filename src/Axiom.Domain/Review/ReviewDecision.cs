using Axiom.Domain.Evaluation;

namespace Axiom.Domain.Review;

public enum ReviewKind
{
    /// <summary>A human authority approves or rejects an evaluation that required review.</summary>
    EvaluationReview,

    /// <summary>A reviewer overturns a single finding as a false positive (feedback for calibration).</summary>
    FindingOverturn,

    /// <summary>An authorized reviewer overrides the significant-change classification (design.md §9).</summary>
    ClassificationOverride,
}

public enum ReviewOutcome
{
    Approved,
    Rejected,
}

/// <summary>
/// An append-only human decision about an evaluation. It is bound to <see cref="SubjectHash"/>: the
/// design's material hash when there is one, otherwise the request fingerprint. A decision therefore
/// stops applying by itself when the reviewed content changes (task 6.10); nothing is ever revoked in place.
/// </summary>
public sealed record ReviewDecision(
    string Id,
    string OrganizationId,
    string EvaluationId,
    ReviewKind Kind,
    ReviewOutcome Outcome,
    string SubjectHash,
    string? FindingCode,
    string Reviewer,
    string Comment,
    DateTimeOffset DecidedAt);

public enum ClearanceState
{
    /// <summary>The verdict needs no human authority.</summary>
    NotRequired,
    Pending,
    Approved,
    Rejected,

    /// <summary>The verdict is BLOCK; review cannot clear a deterministic or precondition failure.</summary>
    Blocked,
}

public sealed record Clearance(ClearanceState State, ReviewDecision? Decision)
{
    public bool AllowsProgress => State is ClearanceState.NotRequired or ClearanceState.Approved;
}

public static class ReviewRules
{
    /// <summary>What a review decision on this evaluation is bound to.</summary>
    public static string SubjectHashOf(EvaluationRecord evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        return evaluation.DesignHash ?? evaluation.RequestFingerprint;
    }

    /// <summary>
    /// Whether human authority has cleared an evaluation. The latest decision bound to the current
    /// subject hash wins; decisions bound to another hash are ignored.
    /// </summary>
    public static Clearance ClearanceOf(EvaluationRecord evaluation, IEnumerable<ReviewDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(decisions);

        if (evaluation.Verdict == Verdict.Block)
        {
            return new Clearance(ClearanceState.Blocked, null);
        }

        if (evaluation.Verdict < Verdict.RequireReview)
        {
            return new Clearance(ClearanceState.NotRequired, null);
        }

        var subject = SubjectHashOf(evaluation);
        var latest = decisions
            .Where(d => d.Kind == ReviewKind.EvaluationReview
                && string.Equals(d.EvaluationId, evaluation.Id, StringComparison.Ordinal)
                && string.Equals(d.SubjectHash, subject, StringComparison.Ordinal))
            .OrderBy(d => d.DecidedAt).ThenBy(d => d.Id, StringComparer.Ordinal)
            .LastOrDefault();

        return latest is null
            ? new Clearance(ClearanceState.Pending, null)
            : new Clearance(latest.Outcome == ReviewOutcome.Approved ? ClearanceState.Approved : ClearanceState.Rejected, latest);
    }

    /// <summary>True when a reviewer overrode the classification of this evaluation to "not significant".</summary>
    public static bool IsSignificanceOverridden(EvaluationRecord evaluation, IEnumerable<ReviewDecision> decisions) =>
        decisions.Any(d => d.Kind == ReviewKind.ClassificationOverride
            && d.Outcome == ReviewOutcome.Approved
            && string.Equals(d.EvaluationId, evaluation.Id, StringComparison.Ordinal)
            && string.Equals(d.SubjectHash, SubjectHashOf(evaluation), StringComparison.Ordinal));
}
