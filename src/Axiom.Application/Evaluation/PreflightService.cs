using Axiom.Application.Common;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;

namespace Axiom.Application.Evaluation;

/// <summary>Input of <c>governance.preflight_change</c> / <c>POST /v1/evaluations/preflight</c>.</summary>
public sealed record PreflightRequest(
    string? Organization,
    string Repository,
    string Ref,
    string Task,
    IReadOnlyCollection<string>? Paths,
    string? Environment,
    string? HarnessType,
    string? HarnessSessionId);

/// <summary>
/// Preflight (R5): before design or editing, tell the caller what the change touches, which
/// authoritative records apply, which exceptions are active, what conflicts exist and what must
/// happen next. Fully deterministic; it needs no model to be correct.
/// </summary>
public sealed class PreflightService(EvaluationPipeline pipeline)
{
    public const string ProduceDesign = "Produce a design and validate it with governance.validate_design before editing.";
    public const string ValidateDiff = "After implementation, call governance.validate_diff on the actual changes.";

    public async Task<StoredEvaluation> ExecuteAsync(AxiomPrincipal principal, PreflightRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(request);
        Authorizer.Demand(principal, AccessRight.Evaluate, request.Organization);
        using var activity = AxiomTelemetry.Source.StartActivity(AxiomTelemetry.Spans.Preflight);

        var session = await pipeline.BeginAsync(
            new EvaluationRequest
            {
                Principal = principal,
                Stage = EvaluationStage.Preflight,
                Repository = RequestLimits.Identifier(request.Repository, "repository"),
                Ref = RequestLimits.Identifier(request.Ref, "ref"),
                Task = RequestLimits.Text(request.Task, "task", RequestLimits.MaxTaskLength) ?? throw AxiomException.Invalid("'task' is required."),
                Paths = RequestLimits.Paths(request.Paths),
                Environment = RequestLimits.OptionalIdentifier(request.Environment, "environment"),
                HarnessType = RequestLimits.OptionalIdentifier(request.HarnessType, "harness.type"),
                HarnessSessionId = RequestLimits.OptionalIdentifier(request.HarnessSessionId, "harness.sessionId"),
            },
            cancellationToken);

        if (session.Existing is null)
        {
            AddStageFindings(session);
        }

        var stored = await pipeline.CompleteAsync(session, cancellationToken);
        activity?.SetTag("axiom.evaluation.id", stored.Evaluation.Id);
        activity?.SetTag("axiom.verdict", VerdictLattice.ToContract(stored.Evaluation.Verdict));
        return stored;
    }

    private static void AddStageFindings(EvaluationSession session)
    {
        if (session.Significance.IsSignificant)
        {
            session.Findings.Add(new Finding
            {
                Code = EvaluationPipeline.DesignRequired,
                Severity = EnforcementLevel.Warn,
                Source = FindingSource.Precondition,
                Message = "This change is classified as significant and requires a validated design before implementation: "
                    + string.Join("; ", session.Significance.Triggers.Select(t => $"{t.Code} ({string.Join(", ", t.Evidence)})")),
                GovernanceIds = [.. session.Significance.Triggers.Where(t => t.SourceRecordId is not null).Select(t => t.SourceRecordId!).Distinct(StringComparer.Ordinal)],
                RecommendedAction = ProduceDesign,
            });
            session.RequiredActions.Add(ProduceDesign);
        }

        foreach (var finding in session.Findings.Where(f => f.Severity >= EnforcementLevel.RequireReview && f.RecommendedAction is not null))
        {
            session.RequiredActions.Add(finding.RecommendedAction!);
        }

        session.RequiredActions.Add(ValidateDiff);
    }
}
