using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Application.Semantic;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Review;
using Axiom.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Axiom.Infrastructure.Audit;

/// <summary>
/// Semantic feedback for calibration (tasks 7.10, 7.11). No table of its own: the append-only
/// <c>review_decision</c> row carries code, evaluation, reviewer, comment and time, and the immutable
/// evaluation document carries provider, model, confidence and governance ids of the finding.
/// </summary>
public sealed class EfSemanticFeedbackStore(AxiomDbContext db) : ISemanticFeedbackStore
{
    public async Task<ImmutableArray<SemanticFindingObservation>> ListRaisedAsync(
        string organizationId, DateTimeOffset since, DateTimeOffset until, int limit, CancellationToken cancellationToken)
    {
        var rows = await db.Set<EvaluationRow>().AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && e.CreatedAt >= since && e.CreatedAt < until && e.FindingCount > 0)
            .OrderBy(e => e.CreatedAt).ThenBy(e => e.Id)
            .Take(Math.Max(1, limit))
            .Select(e => new { e.Id, e.CreatedAt, e.Document })
            .ToListAsync(cancellationToken);

        var observations = new List<SemanticFindingObservation>();
        foreach (var row in rows)
        {
            var evaluation = JsonSerializer.Deserialize<EvaluationRecord>(row.Document, EfEvaluationStore.DocumentOptions)!;
            foreach (var finding in evaluation.Findings.Where(f => f.Source == FindingSource.Semantic))
            {
                var (provider, model) = SemanticFindingInfo.ProviderOf(finding);
                observations.Add(new SemanticFindingObservation(row.Id, finding.Code, provider, model, finding.Confidence ?? 0, finding.GovernanceIds, row.CreatedAt));
            }
        }

        return [.. observations];
    }

    public async Task<ImmutableArray<SemanticFeedback>> ListOverturnsAsync(
        string organizationId, DateTimeOffset since, DateTimeOffset until, int limit, CancellationToken cancellationToken)
    {
        var overturn = ReviewKind.FindingOverturn.ToString();
        var decisions = await db.Set<ReviewDecisionRow>().AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.Kind == overturn && r.DecidedAt >= since && r.DecidedAt < until && r.FindingCode != null)
            .OrderBy(r => r.DecidedAt).ThenBy(r => r.Id)
            .Take(Math.Max(1, limit))
            .ToListAsync(cancellationToken);
        if (decisions.Count == 0)
        {
            return [];
        }

        var ids = decisions.Select(d => d.EvaluationId).Distinct(StringComparer.Ordinal).ToList();
        var documents = await db.Set<EvaluationRow>().AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && ids.Contains(e.Id))
            .Select(e => new { e.Id, e.Document })
            .ToListAsync(cancellationToken);
        var evaluations = documents.ToDictionary(d => d.Id, d => JsonSerializer.Deserialize<EvaluationRecord>(d.Document, EfEvaluationStore.DocumentOptions)!, StringComparer.Ordinal);

        var feedback = new List<SemanticFeedback>();
        var seen = new HashSet<(string, string)>();
        foreach (var decision in decisions)
        {
            // Same lookup as ReviewService.OverturnFindingAsync; a finding overturned twice counts once.
            var finding = evaluations.TryGetValue(decision.EvaluationId, out var evaluation)
                ? evaluation.Findings.FirstOrDefault(f => string.Equals(f.Code, decision.FindingCode, StringComparison.Ordinal))
                : null;
            if (finding is not { Source: FindingSource.Semantic } || !seen.Add((decision.EvaluationId, finding.Code)))
            {
                continue;
            }

            var (provider, model) = SemanticFindingInfo.ProviderOf(finding);
            feedback.Add(new SemanticFeedback(decision.Id, decision.EvaluationId, finding.Code, provider, model, finding.Confidence ?? 0,
                finding.GovernanceIds, decision.Reviewer, decision.Comment, decision.DecidedAt));
        }

        return [.. feedback];
    }
}
