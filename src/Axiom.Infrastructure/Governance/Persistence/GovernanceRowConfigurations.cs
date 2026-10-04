using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Axiom.Infrastructure.Governance.Persistence;

internal static class GovernanceColumns
{
    public const int OrganizationId = 128;
    public const int RecordId = 128;
    public const int Sha = 64;
    public const int Enum = 32;
    public const int Path = 1024;

    public static PropertyBuilder<string> Organization(this PropertyBuilder<string> property) =>
        property.HasColumnName("organization_id").HasMaxLength(OrganizationId);
}

internal sealed class GovernanceSourceRowConfiguration : IEntityTypeConfiguration<GovernanceSourceRow>
{
    public void Configure(EntityTypeBuilder<GovernanceSourceRow> builder)
    {
        builder.ToTable("governance_source");
        builder.HasKey(e => e.OrganizationId).HasName("pk_governance_source");
        builder.Property(e => e.OrganizationId).Organization();
        builder.Property(e => e.RepositoryUrl).HasColumnName("repository_url").HasMaxLength(2048);
        builder.Property(e => e.Branch).HasColumnName("branch").HasMaxLength(255);
        builder.Property(e => e.RootPath).HasColumnName("root_path").HasMaxLength(GovernanceColumns.Path);
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");
    }
}

internal sealed class GovernanceRecordRowConfiguration : IEntityTypeConfiguration<GovernanceRecordRow>
{
    public const string SearchConfig = "english";

    public void Configure(EntityTypeBuilder<GovernanceRecordRow> builder)
    {
        builder.ToTable("governance_record");
        builder.HasKey(e => new { e.OrganizationId, e.RecordId }).HasName("pk_governance_record");
        builder.Property(e => e.OrganizationId).Organization();
        builder.Property(e => e.RecordId).HasColumnName("record_id").HasMaxLength(GovernanceColumns.RecordId);
        builder.Property(e => e.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(GovernanceColumns.Enum);
        builder.Property(e => e.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(GovernanceColumns.Enum);
        builder.Property(e => e.Title).HasColumnName("title");
        builder.Property(e => e.Statement).HasColumnName("statement");
        builder.Property(e => e.Owners).HasColumnName("owners").HasColumnType("text[]");
        builder.Property(e => e.Tags).HasColumnName("tags").HasColumnType("text[]");
        builder.Property(e => e.TagsText).HasColumnName("tags_text");
        builder.Property(e => e.ReviewAfter).HasColumnName("review_after");
        builder.Property(e => e.Revision).HasColumnName("revision");
        builder.Property(e => e.ContentHash).HasColumnName("content_hash").HasMaxLength(GovernanceColumns.Sha);
        builder.Property(e => e.Path).HasColumnName("path").HasMaxLength(GovernanceColumns.Path);
        builder.Property(e => e.BlobSha).HasColumnName("blob_sha").HasMaxLength(GovernanceColumns.Sha);
        builder.Property(e => e.CommitSha).HasColumnName("commit_sha").HasMaxLength(GovernanceColumns.Sha);
        builder.Property(e => e.CommittedAt).HasColumnName("committed_at");
        builder.Property(e => e.Author).HasColumnName("author").HasMaxLength(512);
        builder.Property(e => e.SearchVector).HasColumnName("search_vector");

        builder.HasGeneratedTsVectorColumn(e => e.SearchVector, SearchConfig, e => new { e.RecordId, e.Title, e.Statement, e.TagsText });
        builder.HasIndex(e => e.SearchVector).HasMethod("GIN").HasDatabaseName("ix_governance_record_search");
        builder.HasIndex(e => new { e.OrganizationId, e.Status, e.ReviewAfter }).HasDatabaseName("ix_governance_record_status_review");
        builder.HasIndex(e => new { e.OrganizationId, e.Kind }).HasDatabaseName("ix_governance_record_kind");
    }
}

internal sealed class GovernanceRevisionRowConfiguration : IEntityTypeConfiguration<GovernanceRevisionRow>
{
    public void Configure(EntityTypeBuilder<GovernanceRevisionRow> builder)
    {
        builder.ToTable("governance_revision");
        builder.HasKey(e => new { e.OrganizationId, e.RecordId, e.Revision }).HasName("pk_governance_revision");
        builder.Property(e => e.OrganizationId).Organization();
        builder.Property(e => e.RecordId).HasColumnName("record_id").HasMaxLength(GovernanceColumns.RecordId);
        builder.Property(e => e.Revision).HasColumnName("revision").ValueGeneratedNever();
        builder.Property(e => e.ContentHash).HasColumnName("content_hash").HasMaxLength(GovernanceColumns.Sha);
        builder.Property(e => e.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(GovernanceColumns.Enum);
        builder.Property(e => e.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(GovernanceColumns.Enum);
        builder.Property(e => e.Title).HasColumnName("title");
        builder.Property(e => e.Path).HasColumnName("path").HasMaxLength(GovernanceColumns.Path);
        builder.Property(e => e.BlobSha).HasColumnName("blob_sha").HasMaxLength(GovernanceColumns.Sha);
        builder.Property(e => e.CommitSha).HasColumnName("commit_sha").HasMaxLength(GovernanceColumns.Sha);
        builder.Property(e => e.CommittedAt).HasColumnName("committed_at");
        builder.Property(e => e.Author).HasColumnName("author").HasMaxLength(512);
        builder.Property(e => e.SourceText).HasColumnName("source_text");

        // One revision per distinct content of a record ID.
        builder.HasIndex(e => new { e.OrganizationId, e.RecordId, e.ContentHash }).IsUnique().HasDatabaseName("ux_governance_revision_content");
    }
}

internal sealed class GovernanceScopeRowConfiguration : IEntityTypeConfiguration<GovernanceScopeRow>
{
    public void Configure(EntityTypeBuilder<GovernanceScopeRow> builder)
    {
        builder.ToTable("governance_scope");
        builder.HasKey(e => new { e.OrganizationId, e.RecordId, e.Dimension, e.ValueKey }).HasName("pk_governance_scope");
        builder.Property(e => e.OrganizationId).Organization();
        builder.Property(e => e.RecordId).HasColumnName("record_id").HasMaxLength(GovernanceColumns.RecordId);
        builder.Property(e => e.Dimension).HasColumnName("dimension").HasConversion<string>().HasMaxLength(GovernanceColumns.Enum);
        builder.Property(e => e.ValueKey).HasColumnName("value_key").HasMaxLength(GovernanceColumns.Path);
        builder.Property(e => e.Value).HasColumnName("value").HasMaxLength(GovernanceColumns.Path);
        builder.HasIndex(e => new { e.OrganizationId, e.Dimension, e.ValueKey }).HasDatabaseName("ix_governance_scope_value");
    }
}

internal sealed class GovernanceRelationRowConfiguration : IEntityTypeConfiguration<GovernanceRelationRow>
{
    public void Configure(EntityTypeBuilder<GovernanceRelationRow> builder)
    {
        builder.ToTable("governance_relation");
        builder.HasKey(e => new { e.OrganizationId, e.RecordId, e.Kind, e.TargetId }).HasName("pk_governance_relation");
        builder.Property(e => e.OrganizationId).Organization();
        builder.Property(e => e.RecordId).HasColumnName("record_id").HasMaxLength(GovernanceColumns.RecordId);
        builder.Property(e => e.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(GovernanceColumns.Enum);
        builder.Property(e => e.TargetId).HasColumnName("target_id").HasMaxLength(512);
        builder.HasIndex(e => new { e.OrganizationId, e.TargetId }).HasDatabaseName("ix_governance_relation_target");
    }
}

internal sealed class GovernanceSnapshotRowConfiguration : IEntityTypeConfiguration<GovernanceSnapshotRow>
{
    public void Configure(EntityTypeBuilder<GovernanceSnapshotRow> builder)
    {
        builder.ToTable("governance_snapshot");
        builder.HasKey(e => new { e.OrganizationId, e.SourceCommit }).HasName("pk_governance_snapshot");
        builder.Property(e => e.OrganizationId).Organization();
        builder.Property(e => e.SourceCommit).HasColumnName("source_commit").HasMaxLength(GovernanceColumns.Sha);
        builder.Property(e => e.Sequence).HasColumnName("sequence");
        builder.Property(e => e.SnapshotId).HasColumnName("snapshot_id").HasMaxLength(GovernanceColumns.Sha);
        builder.Property(e => e.RecordCount).HasColumnName("record_count");
        builder.Property(e => e.WarningCount).HasColumnName("warning_count");
        builder.Property(e => e.CommittedAt).HasColumnName("committed_at");
        builder.Property(e => e.Author).HasColumnName("author").HasMaxLength(512);
        builder.Property(e => e.PublishedAt).HasColumnName("published_at");
        builder.HasIndex(e => new { e.OrganizationId, e.Sequence }).IsUnique().HasDatabaseName("ux_governance_snapshot_sequence");
        builder.HasIndex(e => new { e.OrganizationId, e.SnapshotId }).HasDatabaseName("ix_governance_snapshot_id");
    }
}

internal sealed class GovernanceSnapshotEntryRowConfiguration : IEntityTypeConfiguration<GovernanceSnapshotEntryRow>
{
    public void Configure(EntityTypeBuilder<GovernanceSnapshotEntryRow> builder)
    {
        builder.ToTable("governance_snapshot_entry");
        builder.HasKey(e => new { e.OrganizationId, e.RecordId, e.FromSequence }).HasName("pk_governance_snapshot_entry");
        builder.Property(e => e.OrganizationId).Organization();
        builder.Property(e => e.RecordId).HasColumnName("record_id").HasMaxLength(GovernanceColumns.RecordId);
        builder.Property(e => e.FromSequence).HasColumnName("from_sequence").ValueGeneratedNever();
        builder.Property(e => e.Revision).HasColumnName("revision");
        builder.Property(e => e.Path).HasColumnName("path").HasMaxLength(GovernanceColumns.Path);
        builder.Property(e => e.BlobSha).HasColumnName("blob_sha").HasMaxLength(GovernanceColumns.Sha);
        builder.Property(e => e.CommitSha).HasColumnName("commit_sha").HasMaxLength(GovernanceColumns.Sha);
    }
}

internal sealed class SyncCheckpointRowConfiguration : IEntityTypeConfiguration<SyncCheckpointRow>
{
    public void Configure(EntityTypeBuilder<SyncCheckpointRow> builder)
    {
        builder.ToTable("sync_checkpoint");
        builder.HasKey(e => e.OrganizationId).HasName("pk_sync_checkpoint");
        builder.Property(e => e.OrganizationId).Organization();
        builder.Property(e => e.RepositoryUrl).HasColumnName("repository_url").HasMaxLength(2048);
        builder.Property(e => e.Branch).HasColumnName("branch").HasMaxLength(255);
        builder.Property(e => e.RootPath).HasColumnName("root_path").HasMaxLength(GovernanceColumns.Path);
        builder.Property(e => e.HeadCommit).HasColumnName("head_commit").HasMaxLength(GovernanceColumns.Sha);
        builder.Property(e => e.HeadOutcome).HasColumnName("head_outcome").HasConversion<string>().HasMaxLength(GovernanceColumns.Enum);
        builder.Property(e => e.PublishedCommit).HasColumnName("published_commit").HasMaxLength(GovernanceColumns.Sha);
        builder.Property(e => e.PublishedSequence).HasColumnName("published_sequence");
        builder.Property(e => e.SnapshotId).HasColumnName("snapshot_id").HasMaxLength(GovernanceColumns.Sha);
        builder.Property(e => e.RecordCount).HasColumnName("record_count");
        builder.Property(e => e.Issues).HasColumnName("issues").HasColumnType("jsonb");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");
    }
}
