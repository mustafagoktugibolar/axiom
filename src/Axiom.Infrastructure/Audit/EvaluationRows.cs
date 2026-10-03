using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Axiom.Infrastructure.Audit;

/// <summary>An evaluation with its queryable columns and the full record as JSON. Append-only.</summary>
public sealed class EvaluationRow
{
    public required string OrganizationId { get; init; }

    public required string Id { get; init; }

    public required string Stage { get; init; }

    public required string Verdict { get; init; }

    public required string Repository { get; init; }

    public string? Ref { get; init; }

    public string? CommitSha { get; init; }

    public string? BaseSha { get; init; }

    public string? PullRequest { get; init; }

    public required string SnapshotId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string ActorSubject { get; init; }

    public string? HarnessType { get; init; }

    public required bool SignificantChange { get; init; }

    public required int FindingCount { get; init; }

    public required string RequestFingerprint { get; init; }

    public string? ParentEvaluationId { get; init; }

    public string? DesignId { get; init; }

    public required string ReceiptId { get; init; }

    public required string Document { get; init; }
}

/// <summary>One finding of an evaluation, denormalized for governance-health queries.</summary>
public sealed class EvaluationFindingRow
{
    public required string OrganizationId { get; init; }

    public required string EvaluationId { get; init; }

    public required int Ordinal { get; init; }

    public required string Code { get; init; }

    public required string Severity { get; init; }

    public required string Source { get; init; }

    public string? RuleId { get; init; }

    public string? Path { get; init; }

    public string? WaivedBy { get; init; }

    public required string[] GovernanceIds { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Exact governance revision applied by an evaluation (P7), also the source of "last applied" telemetry.</summary>
public sealed class EvaluationAppliedRecordRow
{
    public required string OrganizationId { get; init; }

    public required string EvaluationId { get; init; }

    public required string RecordId { get; init; }

    public required int Revision { get; init; }

    public required string ContentHash { get; init; }

    public required string Importance { get; init; }

    public required bool IsException { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class ReceiptRow
{
    public required string OrganizationId { get; init; }

    public required string Id { get; init; }

    public required string EvaluationId { get; init; }

    /// <summary>Position on the organization's hash chain, starting at 1.</summary>
    public required long Sequence { get; init; }

    public required string Stage { get; init; }

    public required string Verdict { get; init; }

    public required string Repository { get; init; }

    public string? CommitSha { get; init; }

    public required string SnapshotId { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }

    public required string Payload { get; init; }

    public required string Digest { get; init; }

    public required string PreviousChainDigest { get; init; }

    public required string ChainDigest { get; init; }
}

internal sealed class EvaluationRowConfiguration : IEntityTypeConfiguration<EvaluationRow>
{
    public void Configure(EntityTypeBuilder<EvaluationRow> builder)
    {
        builder.ToTable("evaluation");
        builder.HasKey(e => new { e.OrganizationId, e.Id });
        builder.Property(e => e.OrganizationId).HasColumnName("organization_id").HasMaxLength(128);
        builder.Property(e => e.Id).HasColumnName("id").HasMaxLength(64);
        builder.Property(e => e.Stage).HasColumnName("stage").HasMaxLength(32);
        builder.Property(e => e.Verdict).HasColumnName("verdict").HasMaxLength(32);
        builder.Property(e => e.Repository).HasColumnName("repository").HasMaxLength(512);
        builder.Property(e => e.Ref).HasColumnName("ref").HasMaxLength(512);
        builder.Property(e => e.CommitSha).HasColumnName("commit_sha").HasMaxLength(64);
        builder.Property(e => e.BaseSha).HasColumnName("base_sha").HasMaxLength(64);
        builder.Property(e => e.PullRequest).HasColumnName("pull_request").HasMaxLength(128);
        builder.Property(e => e.SnapshotId).HasColumnName("snapshot_id").HasMaxLength(64);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");
        builder.Property(e => e.ActorSubject).HasColumnName("actor_subject").HasMaxLength(256);
        builder.Property(e => e.HarnessType).HasColumnName("harness_type").HasMaxLength(64);
        builder.Property(e => e.SignificantChange).HasColumnName("significant_change");
        builder.Property(e => e.FindingCount).HasColumnName("finding_count");
        builder.Property(e => e.RequestFingerprint).HasColumnName("request_fingerprint").HasMaxLength(64);
        builder.Property(e => e.ParentEvaluationId).HasColumnName("parent_evaluation_id").HasMaxLength(64);
        builder.Property(e => e.DesignId).HasColumnName("design_id").HasMaxLength(256);
        builder.Property(e => e.ReceiptId).HasColumnName("receipt_id").HasMaxLength(64);
        builder.Property(e => e.Document).HasColumnName("document").HasColumnType("jsonb");
        builder.HasIndex(e => new { e.OrganizationId, e.CreatedAt }).HasDatabaseName("ix_evaluation_created").IsDescending(false, true);
        builder.HasIndex(e => new { e.OrganizationId, e.Repository, e.CommitSha }).HasDatabaseName("ix_evaluation_commit");
    }
}

internal sealed class EvaluationFindingRowConfiguration : IEntityTypeConfiguration<EvaluationFindingRow>
{
    public void Configure(EntityTypeBuilder<EvaluationFindingRow> builder)
    {
        builder.ToTable("evaluation_finding");
        builder.HasKey(e => new { e.OrganizationId, e.EvaluationId, e.Ordinal });
        builder.Property(e => e.OrganizationId).HasColumnName("organization_id").HasMaxLength(128);
        builder.Property(e => e.EvaluationId).HasColumnName("evaluation_id").HasMaxLength(64);
        builder.Property(e => e.Ordinal).HasColumnName("ordinal");
        builder.Property(e => e.Code).HasColumnName("code").HasMaxLength(128);
        builder.Property(e => e.Severity).HasColumnName("severity").HasMaxLength(32);
        builder.Property(e => e.Source).HasColumnName("source").HasMaxLength(32);
        builder.Property(e => e.RuleId).HasColumnName("rule_id").HasMaxLength(128);
        builder.Property(e => e.Path).HasColumnName("path").HasMaxLength(1024);
        builder.Property(e => e.WaivedBy).HasColumnName("waived_by").HasMaxLength(128);
        builder.Property(e => e.GovernanceIds).HasColumnName("governance_ids").HasColumnType("text[]");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");
        builder.HasIndex(e => new { e.OrganizationId, e.Code, e.CreatedAt }).HasDatabaseName("ix_evaluation_finding_code");
        builder.HasIndex(e => e.GovernanceIds).HasDatabaseName("ix_evaluation_finding_governance").HasMethod("gin");
    }
}

internal sealed class EvaluationAppliedRecordRowConfiguration : IEntityTypeConfiguration<EvaluationAppliedRecordRow>
{
    public void Configure(EntityTypeBuilder<EvaluationAppliedRecordRow> builder)
    {
        builder.ToTable("evaluation_applied_record");
        builder.HasKey(e => new { e.OrganizationId, e.EvaluationId, e.RecordId });
        builder.Property(e => e.OrganizationId).HasColumnName("organization_id").HasMaxLength(128);
        builder.Property(e => e.EvaluationId).HasColumnName("evaluation_id").HasMaxLength(64);
        builder.Property(e => e.RecordId).HasColumnName("record_id").HasMaxLength(128);
        builder.Property(e => e.Revision).HasColumnName("revision");
        builder.Property(e => e.ContentHash).HasColumnName("content_hash").HasMaxLength(64);
        builder.Property(e => e.Importance).HasColumnName("importance").HasMaxLength(32);
        builder.Property(e => e.IsException).HasColumnName("is_exception");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");
        builder.HasIndex(e => new { e.OrganizationId, e.RecordId, e.CreatedAt }).HasDatabaseName("ix_evaluation_applied_record_usage");
    }
}

internal sealed class ReceiptRowConfiguration : IEntityTypeConfiguration<ReceiptRow>
{
    public void Configure(EntityTypeBuilder<ReceiptRow> builder)
    {
        builder.ToTable("receipt");
        builder.HasKey(e => new { e.OrganizationId, e.Id });
        builder.Property(e => e.OrganizationId).HasColumnName("organization_id").HasMaxLength(128);
        builder.Property(e => e.Id).HasColumnName("id").HasMaxLength(64);
        builder.Property(e => e.EvaluationId).HasColumnName("evaluation_id").HasMaxLength(64);
        builder.Property(e => e.Sequence).HasColumnName("sequence");
        builder.Property(e => e.Stage).HasColumnName("stage").HasMaxLength(32);
        builder.Property(e => e.Verdict).HasColumnName("verdict").HasMaxLength(32);
        builder.Property(e => e.Repository).HasColumnName("repository").HasMaxLength(512);
        builder.Property(e => e.CommitSha).HasColumnName("commit_sha").HasMaxLength(64);
        builder.Property(e => e.SnapshotId).HasColumnName("snapshot_id").HasMaxLength(64);
        builder.Property(e => e.IssuedAt).HasColumnName("issued_at");
        builder.Property(e => e.Payload).HasColumnName("payload");
        builder.Property(e => e.Digest).HasColumnName("digest").HasMaxLength(64);
        builder.Property(e => e.PreviousChainDigest).HasColumnName("previous_chain_digest").HasMaxLength(64);
        builder.Property(e => e.ChainDigest).HasColumnName("chain_digest").HasMaxLength(64);
        builder.HasIndex(e => new { e.OrganizationId, e.EvaluationId }).IsUnique().HasDatabaseName("ux_receipt_evaluation");
        builder.HasIndex(e => new { e.OrganizationId, e.Sequence }).IsUnique().HasDatabaseName("ux_receipt_sequence");
        builder.HasIndex(e => new { e.OrganizationId, e.Repository, e.CommitSha }).HasDatabaseName("ix_receipt_commit");
    }
}
