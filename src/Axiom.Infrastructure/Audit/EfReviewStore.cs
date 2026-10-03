using System.Collections.Immutable;
using System.Text.Json;
using Axiom.Application.Evaluation;
using Axiom.Application.Review;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Review;
using Axiom.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Axiom.Infrastructure.Audit;

public sealed class ReviewDecisionRow
{
    public required string OrganizationId { get; init; }

    public required string Id { get; init; }

    public required string EvaluationId { get; init; }

    public required string Kind { get; init; }

    public required string Outcome { get; init; }

    public required string SubjectHash { get; init; }

    public string? FindingCode { get; init; }

    public required string Reviewer { get; init; }

    public required string Comment { get; init; }

    public required DateTimeOffset DecidedAt { get; init; }
}

internal sealed class ReviewDecisionRowConfiguration : IEntityTypeConfiguration<ReviewDecisionRow>
{
    public void Configure(EntityTypeBuilder<ReviewDecisionRow> builder)
    {
        builder.ToTable("review_decision");
        builder.HasKey(e => new { e.OrganizationId, e.Id });
        builder.Property(e => e.OrganizationId).HasColumnName("organization_id").HasMaxLength(128);
        builder.Property(e => e.Id).HasColumnName("id").HasMaxLength(64);
        builder.Property(e => e.EvaluationId).HasColumnName("evaluation_id").HasMaxLength(64);
        builder.Property(e => e.Kind).HasColumnName("kind").HasMaxLength(32);
        builder.Property(e => e.Outcome).HasColumnName("outcome").HasMaxLength(16);
        builder.Property(e => e.SubjectHash).HasColumnName("subject_hash").HasMaxLength(64);
        builder.Property(e => e.FindingCode).HasColumnName("finding_code").HasMaxLength(128);
        builder.Property(e => e.Reviewer).HasColumnName("reviewer").HasMaxLength(256);
        builder.Property(e => e.Comment).HasColumnName("comment").HasMaxLength(4000);
        builder.Property(e => e.DecidedAt).HasColumnName("decided_at");
        builder.HasIndex(e => new { e.OrganizationId, e.EvaluationId }).HasDatabaseName("ix_review_decision_evaluation");
        builder.HasIndex(e => new { e.OrganizationId, e.Kind, e.FindingCode, e.DecidedAt }).HasDatabaseName("ix_review_decision_feedback");
    }
}

/// <summary>Append-only review decisions (audit on all approvals, design.md §14).</summary>
public sealed class EfReviewStore(AxiomDbContext db) : IReviewStore
{
    public async Task AppendAsync(ReviewDecision decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        db.Set<ReviewDecisionRow>().Add(new ReviewDecisionRow
        {
            OrganizationId = decision.OrganizationId,
            Id = decision.Id,
            EvaluationId = decision.EvaluationId,
            Kind = decision.Kind.ToString(),
            Outcome = decision.Outcome.ToString(),
            SubjectHash = decision.SubjectHash,
            FindingCode = decision.FindingCode,
            Reviewer = decision.Reviewer,
            Comment = decision.Comment,
            DecidedAt = decision.DecidedAt,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ImmutableArray<ReviewDecision>> ListAsync(string organizationId, string evaluationId, CancellationToken cancellationToken)
    {
        var rows = await db.Set<ReviewDecisionRow>().AsNoTracking()
            .Where(r => r.OrganizationId == organizationId && r.EvaluationId == evaluationId)
            .OrderBy(r => r.DecidedAt).ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);
        return [.. rows.Select(ToDomain)];
    }

    public async Task<ImmutableArray<ReviewQueueItem>> ListPendingAsync(string organizationId, int skip, int take, CancellationToken cancellationToken)
    {
        var review = ReviewKind.EvaluationReview.ToString();
        var requireReview = VerdictLattice.ToContract(Verdict.RequireReview);
        var rows = await db.Set<EvaluationRow>().AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && e.Verdict == requireReview)
            .Where(e => !db.Set<ReviewDecisionRow>().Any(r => r.OrganizationId == organizationId && r.EvaluationId == e.Id && r.Kind == review))
            .OrderByDescending(e => e.CreatedAt).ThenBy(e => e.Id)
            .Skip(skip).Take(take)
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row =>
            {
                var evaluation = JsonSerializer.Deserialize<EvaluationRecord>(row.Document, EfEvaluationStore.DocumentOptions)!;
                var summary = new EvaluationSummary(
                    row.Id, evaluation.Stage, evaluation.Verdict, row.Repository, row.CommitSha, row.PullRequest, row.SnapshotId, row.CreatedAt,
                    row.ActorSubject, row.HarnessType, row.SignificantChange, row.FindingCount, row.ReceiptId);
                return new ReviewQueueItem(summary, evaluation.RequiredReviewers,
                    [.. evaluation.Findings.Where(f => !f.IsWaived && f.Severity >= Domain.Governance.EnforcementLevel.RequireReview).Select(f => f.Code).Distinct(StringComparer.Ordinal)]);
            }),
        ];
    }

    private static ReviewDecision ToDomain(ReviewDecisionRow r) => new(
        r.Id, r.OrganizationId, r.EvaluationId, Enum.Parse<ReviewKind>(r.Kind), Enum.Parse<ReviewOutcome>(r.Outcome),
        r.SubjectHash, r.FindingCode, r.Reviewer, r.Comment, r.DecidedAt);
}
