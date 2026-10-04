using System.Collections.Immutable;
using System.Globalization;
using Axiom.Application.Common;
using Axiom.Application.Policy;
using Axiom.Application.Review;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Axiom.Domain.Review;

namespace Axiom.Application.Evaluation;

/// <summary>
/// Input of <c>governance.validate_diff</c> / <c>POST /v1/evaluations/diff</c>, and of the pull-request
/// gate (<c>POST /v1/evaluations/pr</c>), which additionally names the pull request.
/// </summary>
public sealed record DiffValidationRequest(
    string? Organization,
    string Repository,
    string BaseSha,
    string HeadSha,
    string? Ref,
    string? PullRequest,
    string? DesignEvaluationId,
    string? HarnessType,
    string? HarnessSessionId);

/// <summary>Stable codes of the findings the diff stage adds on top of policy findings.</summary>
public static class DiffFindingCodes
{
    public const string ScopeExpansion = "SCOPE_EXPANSION";
    public const string DesignBlocked = "DESIGN_BLOCKED";
    public const string DesignRejected = "DESIGN_REJECTED";
    public const string DesignApprovalPending = "DESIGN_APPROVAL_PENDING";
    public const string DiffTruncated = "DIFF_TRUNCATED";
}

/// <summary>
/// Diff and pull-request validation (R7, R8, design.md §8.3–8.4). The actual diff, addressed by
/// immutable commit SHAs, is authoritative: scope is recomputed from it, expansion beyond the validated
/// design is reported, and every applicable deterministic rule runs on the changed files. The pull
/// request stage re-evaluates independently of any earlier local result and resolves authority
/// conflicts as a hard gate.
/// </summary>
public sealed class DiffEvaluationService(
    EvaluationPipeline pipeline,
    IScmDiffSource scm,
    PolicyEngine policy,
    IPolicyRuleCatalog catalog,
    IEvaluationStore evaluations,
    IReviewStore reviews)
{
    public const string RevalidateDesign = "Validate a design that covers the full scope of this change with governance.validate_design, then validate the diff again with that design evaluation.";
    public const string RequestDesign = "Produce a design for this significant change and validate it with governance.validate_design, then validate the diff again with that design evaluation.";

    public Task<StoredEvaluation> ExecuteAsync(AxiomPrincipal principal, DiffValidationRequest request, CancellationToken cancellationToken) =>
        RunAsync(principal, request, request?.PullRequest is null ? EvaluationStage.Diff : EvaluationStage.PullRequest, cancellationToken);

    /// <summary>The merge gate: the pull request must be named and the evaluation is a hard gate.</summary>
    public Task<StoredEvaluation> ExecutePullRequestAsync(AxiomPrincipal principal, DiffValidationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.PullRequest))
        {
            throw AxiomException.Invalid("'pullRequest' is required.");
        }

        return RunAsync(principal, request, EvaluationStage.PullRequest, cancellationToken);
    }

    private async Task<StoredEvaluation> RunAsync(AxiomPrincipal principal, DiffValidationRequest request, EvaluationStage stage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        Authorizer.Demand(principal, AccessRight.Evaluate, request.Organization);
        using var activity = AxiomTelemetry.Source.StartActivity(AxiomTelemetry.Spans.DiffValidate);

        var repository = RequestLimits.Identifier(request.Repository, "repository");
        var baseSha = RequestLimits.CommitSha(request.BaseSha, "baseSha", required: true)!;
        var headSha = RequestLimits.CommitSha(request.HeadSha, "headSha", required: true)!;
        var designId = RequestLimits.OptionalIdentifier(request.DesignEvaluationId, "designEvaluationId");

        var design = designId is null ? null : await LoadDesignAsync(principal, designId, cancellationToken);
        var diff = await scm.GetDiffAsync(new ScmDiffRequest(principal.OrganizationId, repository, baseSha, headSha), cancellationToken);

        var paths = RequestLimits.Paths([.. diff.Files.SelectMany(f => f.PreviousPath is null ? [f.Path] : new[] { f.Path, f.PreviousPath })]) ?? [];
        var session = await pipeline.BeginAsync(
            new EvaluationRequest
            {
                Principal = principal,
                Stage = stage,
                Repository = repository,
                Ref = RequestLimits.OptionalIdentifier(request.Ref, "ref"),
                CommitSha = headSha,
                BaseSha = baseSha,
                PullRequest = RequestLimits.OptionalIdentifier(request.PullRequest, "pullRequest"),
                Paths = paths,
                HarnessType = RequestLimits.OptionalIdentifier(request.HarnessType, "harness.type"),
                HarnessSessionId = RequestLimits.OptionalIdentifier(request.HarnessSessionId, "harness.sessionId"),
                ParentEvaluationId = design?.Stored.Evaluation.Id,
                DesignId = design?.Stored.Evaluation.DesignId,
                DesignHash = design?.Stored.Evaluation.DesignHash,
                HardGate = stage == EvaluationStage.PullRequest,
                FingerprintExtras =
                [
                    "policy:" + PolicyCatalogHash(),
                    "design-clearance:" + (design?.Clearance.State.ToString() ?? "none"),
                    "design-significance-override:" + (design?.Overridden ?? false),
                    "truncated:" + diff.Truncated,
                ],
            },
            cancellationToken);

        if (session.Existing is null)
        {
            AnalyzeDesignLineage(session, design);
            AddTruncationFinding(session, diff);
            RunPolicies(session, diff);

            foreach (var finding in session.Findings.Where(f => !f.IsWaived && f.Severity >= EnforcementLevel.RequireReview && f.RecommendedAction is not null))
            {
                session.RequiredActions.Add(finding.RecommendedAction!);
            }
        }

        var stored = await pipeline.CompleteAsync(session, cancellationToken);
        activity?.SetTag("axiom.evaluation.id", stored.Evaluation.Id);
        activity?.SetTag("axiom.verdict", VerdictLattice.ToContract(stored.Evaluation.Verdict));
        return stored;
    }

    private sealed record DesignLineage(StoredEvaluation Stored, Clearance Clearance, bool Overridden);

    private async Task<DesignLineage> LoadDesignAsync(AxiomPrincipal principal, string designId, CancellationToken cancellationToken)
    {
        var stored = await evaluations.FindAsync(principal.OrganizationId, designId, cancellationToken)
            ?? throw AxiomException.NotFound($"Design evaluation '{designId}'");
        if (stored.Evaluation.Stage != EvaluationStage.Design)
        {
            throw AxiomException.Invalid($"Evaluation '{designId}' is a {EvaluationResult.StageName(stored.Evaluation.Stage)} evaluation, not a DESIGN evaluation.");
        }

        var decisions = await reviews.ListAsync(principal.OrganizationId, stored.Evaluation.Id, cancellationToken);
        return new DesignLineage(stored, ReviewRules.ClearanceOf(stored.Evaluation, decisions), ReviewRules.IsSignificanceOverridden(stored.Evaluation, decisions));
    }

    private static void AnalyzeDesignLineage(EvaluationSession session, DesignLineage? design)
    {
        if (design is null)
        {
            if (session.Significance.IsSignificant)
            {
                session.Findings.Add(DesignRequiredFinding(session));
            }

            return;
        }

        var evaluation = design.Stored.Evaluation;
        var designEvidence = new EvidenceRef("evaluation", evaluation.Id, Hash: evaluation.DesignHash);

        switch (design.Clearance.State)
        {
            case ClearanceState.Blocked:
                session.Findings.Add(Design(DiffFindingCodes.DesignBlocked, EnforcementLevel.Block,
                    $"The design evaluation {evaluation.Id} has a BLOCK verdict, so its design does not authorize implementation.",
                    "Resolve the findings of the design evaluation and validate the design again.", designEvidence));
                break;
            case ClearanceState.Rejected:
                session.Findings.Add(Design(DiffFindingCodes.DesignRejected, EnforcementLevel.Block,
                    $"A reviewer rejected the design evaluation {evaluation.Id}.",
                    "Revise the design to address the reviewer's decision and validate it again.", designEvidence));
                break;
            case ClearanceState.Pending:
                session.Findings.Add(Design(DiffFindingCodes.DesignApprovalPending, EnforcementLevel.RequireReview,
                    $"The design evaluation {evaluation.Id} requires review that has not been given yet.",
                    $"Obtain review of design evaluation {evaluation.Id} from {OwnersText(evaluation)}.", designEvidence));
                break;
        }

        var expansion = ScopeExpansionAnalyzer.Analyze(evaluation, ResolvedScopeView.From(session.Context.Scope, session.Context.Gaps), session.Significance, design.Overridden);
        if (expansion.IsExpanded)
        {
            var parts = new List<string>();
            if (!expansion.NewScope.IsEmpty)
            {
                parts.Add("outside the validated scope: " + string.Join(", ", expansion.NewScope));
            }

            if (!expansion.NewChangeClasses.IsEmpty)
            {
                parts.Add("change classes the design did not cover: " + string.Join(", ", expansion.NewChangeClasses));
            }

            if (expansion.SignificantWithoutSignificantDesign)
            {
                parts.Add("the diff is a significant change but the design was classified as not significant");
            }

            session.Findings.Add(Design(DiffFindingCodes.ScopeExpansion, EnforcementLevel.RequireReview,
                $"The actual diff exceeds the design evaluation {evaluation.Id}; its approval no longer covers the change: {string.Join("; ", parts)}.",
                RevalidateDesign, designEvidence));
        }
    }

    private static Finding DesignRequiredFinding(EvaluationSession session) => new()
    {
        Code = EvaluationPipeline.DesignRequired,
        Severity = EnforcementLevel.RequireReview,
        Source = FindingSource.Precondition,
        Message = "This change is classified as significant and no validated design is referenced: "
            + string.Join("; ", session.Significance.Triggers.Select(t => $"{t.Code} ({string.Join(", ", t.Evidence)})")),
        GovernanceIds = [.. session.Significance.Triggers.Where(t => t.SourceRecordId is not null).Select(t => t.SourceRecordId!).Distinct(StringComparer.Ordinal)],
        RecommendedAction = RequestDesign,
    };

    private static Finding Design(string code, EnforcementLevel severity, string message, string action, EvidenceRef evidence) => new()
    {
        Code = code,
        Severity = severity,
        Source = FindingSource.Precondition,
        Message = message,
        RecommendedAction = action,
        Evidence = [evidence],
    };

    private static string OwnersText(EvaluationRecord evaluation) =>
        evaluation.RequiredReviewers.IsDefaultOrEmpty ? "the owners of the applicable records" : string.Join(", ", evaluation.RequiredReviewers);

    private static void AddTruncationFinding(EvaluationSession session, ScmDiff diff)
    {
        if (!diff.Truncated)
        {
            return;
        }

        session.Findings.Add(new Finding
        {
            Code = DiffFindingCodes.DiffTruncated,
            Severity = EnforcementLevel.RequireReview,
            Source = FindingSource.Precondition,
            Message = $"The diff changes {diff.TotalFiles.ToString(CultureInfo.InvariantCulture)} files; only the first {diff.Files.Length.ToString(CultureInfo.InvariantCulture)} in path order were evaluated.",
            RecommendedAction = "Split the change into smaller commits, or have a reviewer confirm the files that were not evaluated.",
            Evidence = [new EvidenceRef("scm-diff", Locator: $"{diff.Files.Length}/{diff.TotalFiles}")],
        });
    }

    private void RunPolicies(EvaluationSession session, ScmDiff diff)
    {
        using var activity = AxiomTelemetry.Source.StartActivity(AxiomTelemetry.Spans.PolicyRun);
        var evaluation = policy.Evaluate(
            new Axiom.Domain.Policy.PolicyContext(session.Context.RepositoryName, diff.Files, diff.Tree, session.Context.Scope),
            session.Resolution);
        session.PolicyRuns.AddRange(evaluation.Results);
        session.Findings.AddRange(evaluation.Findings);
        activity?.SetTag("axiom.policy.runs", evaluation.Results.Length);
        activity?.SetTag("axiom.policy.findings", evaluation.Findings.Length);
    }

    /// <summary>Identifies the exact rule set (IDs and versions) so a rule change yields a new evaluation, never a cached one.</summary>
    private string PolicyCatalogHash() =>
        PolicyEngine.ComputeRuleHash("catalog", "1", catalog.Describe().Select(d => new KeyValuePair<string, string>(d.RuleId, d.Version)));
}
