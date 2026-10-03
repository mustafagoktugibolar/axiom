using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Review;

namespace Axiom.Application.Review;

public sealed record ReviewQueueItem(EvaluationSummary Evaluation, ImmutableArray<string> RequiredReviewers, ImmutableArray<string> FindingCodes);

/// <summary>Append-only store of review decisions and the queue of evaluations awaiting one.</summary>
public interface IReviewStore
{
    Task AppendAsync(ReviewDecision decision, CancellationToken cancellationToken);

    Task<ImmutableArray<ReviewDecision>> ListAsync(string organizationId, string evaluationId, CancellationToken cancellationToken);

    /// <summary>Evaluations whose verdict is REQUIRE_REVIEW and that have no review decision yet, newest first.</summary>
    Task<ImmutableArray<ReviewQueueItem>> ListPendingAsync(string organizationId, int skip, int take, CancellationToken cancellationToken);
}

public sealed record ReviewRequest(string EvaluationId, bool Approve, string Comment);

public sealed record FindingOverturnRequest(string EvaluationId, string FindingCode, string Comment);

public sealed record EvaluationReviewState(StoredEvaluation Stored, Clearance Clearance, ImmutableArray<ReviewDecision> Decisions);

/// <summary>
/// Human authority over evaluations (tasks 6.9, 6.10, 7.10). Review never changes an evaluation or a
/// governance record; it adds a decision that later stages take into account.
/// </summary>
public sealed class ReviewService(IEvaluationStore evaluations, IReviewStore reviews, TimeProvider time)
{
    public const int MinimumCommentLength = 10;

    public async Task<EvaluationReviewState> GetAsync(AxiomPrincipal principal, string evaluationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.ReadAudit);
        var stored = await LoadAsync(principal, evaluationId, cancellationToken);
        var decisions = await reviews.ListAsync(principal.OrganizationId, stored.Evaluation.Id, cancellationToken);
        return new EvaluationReviewState(stored, ReviewRules.ClearanceOf(stored.Evaluation, decisions), decisions);
    }

    public Task<ImmutableArray<ReviewQueueItem>> ListPendingAsync(AxiomPrincipal principal, int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.ReviewDesign);
        return reviews.ListPendingAsync(principal.OrganizationId, Math.Max(0, skip), Math.Clamp(take, 1, 200), cancellationToken);
    }

    /// <summary>Approves or rejects an evaluation that required review.</summary>
    public async Task<ReviewDecision> ReviewAsync(AxiomPrincipal principal, ReviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        Authorizer.Demand(principal, AccessRight.ReviewDesign);
        var stored = await LoadAsync(principal, request.EvaluationId, cancellationToken);
        var evaluation = stored.Evaluation;

        if (evaluation.Verdict == Verdict.Block)
        {
            throw new AxiomException(ErrorKind.Conflict, ErrorCodes.Conflict,
                "A BLOCK verdict cannot be cleared by review. Fix the violation, or request a scoped exception if the control is exemptable.");
        }

        if (evaluation.Verdict < Verdict.RequireReview)
        {
            throw new AxiomException(ErrorKind.Conflict, ErrorCodes.Conflict, "This evaluation does not require review.");
        }

        EnsureIndependent(principal, evaluation);
        EnsureEntitled(principal, evaluation);

        return await AppendAsync(principal, evaluation, ReviewKind.EvaluationReview,
            request.Approve ? ReviewOutcome.Approved : ReviewOutcome.Rejected, null, request.Comment, cancellationToken);
    }

    /// <summary>Records that a reviewer considers a finding a false positive. Feeds calibration metrics.</summary>
    public async Task<ReviewDecision> OverturnFindingAsync(AxiomPrincipal principal, FindingOverturnRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        Authorizer.Demand(principal, AccessRight.SubmitFeedback);
        var stored = await LoadAsync(principal, request.EvaluationId, cancellationToken);
        var finding = stored.Evaluation.Findings.FirstOrDefault(f => string.Equals(f.Code, request.FindingCode, StringComparison.Ordinal))
            ?? throw AxiomException.NotFound($"Finding '{request.FindingCode}' on evaluation '{request.EvaluationId}'");

        var decision = await AppendAsync(principal, stored.Evaluation, ReviewKind.FindingOverturn, ReviewOutcome.Rejected, finding.Code, request.Comment, cancellationToken);
        if (finding.Source == FindingSource.Semantic)
        {
            AxiomTelemetry.SemanticOverturns.Add(1, new KeyValuePair<string, object?>("code", finding.Code));
        }

        return decision;
    }

    /// <summary>Overrides a "significant change" classification so that no formal design is required.</summary>
    public async Task<ReviewDecision> OverrideClassificationAsync(AxiomPrincipal principal, string evaluationId, string comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.OverrideClassification);
        var stored = await LoadAsync(principal, evaluationId, cancellationToken);
        if (!stored.Evaluation.Significance.IsSignificant)
        {
            throw new AxiomException(ErrorKind.Conflict, ErrorCodes.Conflict, "This evaluation is not classified as significant.");
        }

        EnsureIndependent(principal, stored.Evaluation);
        return await AppendAsync(principal, stored.Evaluation, ReviewKind.ClassificationOverride, ReviewOutcome.Approved, null, comment, cancellationToken);
    }

    private async Task<StoredEvaluation> LoadAsync(AxiomPrincipal principal, string evaluationId, CancellationToken cancellationToken) =>
        await evaluations.FindAsync(principal.OrganizationId, RequestLimits.Identifier(evaluationId, "evaluationId"), cancellationToken)
        ?? throw AxiomException.NotFound($"Evaluation '{evaluationId}'");

    private static void EnsureIndependent(AxiomPrincipal principal, EvaluationRecord evaluation)
    {
        if (string.Equals(principal.Subject, evaluation.Actor.Subject, StringComparison.Ordinal))
        {
            throw AxiomException.Forbidden("The author of a change cannot review it.");
        }
    }

    /// <summary>Approvers may review anything; other reviewers only what is routed to one of their teams.</summary>
    private static void EnsureEntitled(AxiomPrincipal principal, EvaluationRecord evaluation)
    {
        if (principal.IsInRole(Role.Approver))
        {
            return;
        }

        var routed = evaluation.RequiredReviewers.Any(reviewer =>
            principal.Teams.Any(team => string.Equals(team, reviewer, StringComparison.OrdinalIgnoreCase)
                || string.Equals($"team:{team}", reviewer, StringComparison.OrdinalIgnoreCase)));
        if (!routed)
        {
            throw AxiomException.Forbidden("This review is routed to other owners: " + string.Join(", ", evaluation.RequiredReviewers));
        }
    }

    private async Task<ReviewDecision> AppendAsync(
        AxiomPrincipal principal, EvaluationRecord evaluation, ReviewKind kind, ReviewOutcome outcome, string? findingCode, string comment, CancellationToken cancellationToken)
    {
        var text = RequestLimits.Text(comment, "comment", 4_000)?.Trim() ?? string.Empty;
        if (text.Length < MinimumCommentLength)
        {
            throw AxiomException.Invalid($"A review needs a rationale of at least {MinimumCommentLength} characters.");
        }

        var now = time.GetUtcNow();
        var subject = ReviewRules.SubjectHashOf(evaluation);
        var id = "rv_" + EvaluationIdentity.Fingerprint(evaluation.Id, kind.ToString(), findingCode, principal.Subject, subject, now.ToString("O"))[..24];
        var decision = new ReviewDecision(id, principal.OrganizationId, evaluation.Id, kind, outcome, subject, findingCode, principal.Subject, text, now);
        await reviews.AppendAsync(decision, cancellationToken);
        return decision;
    }
}
