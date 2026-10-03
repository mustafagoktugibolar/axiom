using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Axiom.Infrastructure.Catalog.Persistence;

/// <summary>
/// Effective node of the System Graph: the view of an entity held by its most trusted source. What each
/// individual source reported is kept in <see cref="SoftwareEntityObservationRow"/>.
/// </summary>
public sealed class SoftwareEntityRow
{
    public required string OrganizationId { get; init; }

    /// <summary>Canonical reference, e.g. <c>component:gui/api-gateway</c>.</summary>
    public required string EntityRef { get; init; }

    public required string Kind { get; init; }

    public required string Name { get; init; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public required string[] Technologies { get; set; }

    /// <summary>String attributes as a JSON object.</summary>
    public required string Attributes { get; set; }

    public required string SourceType { get; set; }

    public required string SourceLocator { get; set; }

    public required DateTimeOffset FirstSeen { get; set; }

    public required DateTimeOffset LastSeen { get; set; }

    public required double Confidence { get; set; }

    public required bool Confirmed { get; set; }

    /// <summary>Declared or confirmed; stored so queries can exclude candidates without knowing the trust rules.</summary>
    public required bool IsFact { get; set; }
}

/// <summary>Effective typed edge of the System Graph.</summary>
public sealed class SoftwareEdgeRow
{
    public required string OrganizationId { get; init; }

    public required string SourceRef { get; init; }

    public required string Relation { get; init; }

    public required string TargetRef { get; init; }

    public required string SourceType { get; set; }

    public required string SourceLocator { get; set; }

    public required DateTimeOffset FirstSeen { get; set; }

    public required DateTimeOffset LastSeen { get; set; }

    public required double Confidence { get; set; }

    public required bool Confirmed { get; set; }

    public required bool IsFact { get; set; }

    /// <summary>Who confirmed an inferred edge. Survives re-imports for as long as any source still reports the edge.</summary>
    public string? ConfirmedBy { get; set; }

    public DateTimeOffset? ConfirmedAt { get; set; }
}

/// <summary>What one source locator reported about an entity.</summary>
public sealed class SoftwareEntityObservationRow
{
    public required string OrganizationId { get; init; }

    public required string EntityRef { get; init; }

    public required string SourceLocator { get; init; }

    public required string SourceType { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    public required string[] Technologies { get; set; }

    public required string Attributes { get; set; }

    public required DateTimeOffset FirstSeen { get; set; }

    public required DateTimeOffset LastSeen { get; set; }

    public required double Confidence { get; set; }

    public required bool Confirmed { get; set; }
}

/// <summary>What one source locator reported about an edge.</summary>
public sealed class SoftwareEdgeObservationRow
{
    public required string OrganizationId { get; init; }

    public required string SourceRef { get; init; }

    public required string Relation { get; init; }

    public required string TargetRef { get; init; }

    public required string SourceLocator { get; init; }

    public required string SourceType { get; set; }

    public required DateTimeOffset FirstSeen { get; set; }

    public required DateTimeOffset LastSeen { get; set; }

    public required double Confidence { get; set; }

    public required bool Confirmed { get; set; }
}

/// <summary>One way a repository entity can be addressed: name, simple name, declared alias, clone URL or URL path.</summary>
public sealed class RepositoryBindingRow
{
    public required string OrganizationId { get; init; }

    public required string Alias { get; init; }

    public required string AliasType { get; init; }

    public required string RepositoryRef { get; init; }
}

/// <summary>Per-organization catalog version counter.</summary>
public sealed class CatalogStateRow
{
    public required string OrganizationId { get; init; }

    public long Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

internal static class CatalogColumns
{
    public const int OrganizationLength = 128;
    public const int RefLength = 400;
    public const int RelationLength = 32;
    public const int SourceTypeLength = 32;
    public const int LocatorLength = 512;
}

internal sealed class SoftwareEntityRowConfiguration : IEntityTypeConfiguration<SoftwareEntityRow>
{
    public void Configure(EntityTypeBuilder<SoftwareEntityRow> builder)
    {
        builder.ToTable("software_entity");
        builder.HasKey(e => new { e.OrganizationId, e.EntityRef });
        builder.Property(e => e.OrganizationId).HasColumnName("organization_id").HasMaxLength(CatalogColumns.OrganizationLength);
        builder.Property(e => e.EntityRef).HasColumnName("entity_ref").HasMaxLength(CatalogColumns.RefLength);
        builder.Property(e => e.Kind).HasColumnName("kind").HasMaxLength(32);
        builder.Property(e => e.Name).HasColumnName("name").HasMaxLength(CatalogColumns.RefLength);
        builder.Property(e => e.Title).HasColumnName("title").HasMaxLength(512);
        builder.Property(e => e.Description).HasColumnName("description");
        builder.Property(e => e.Technologies).HasColumnName("technologies").HasColumnType("text[]");
        builder.Property(e => e.Attributes).HasColumnName("attributes").HasColumnType("jsonb");
        builder.Property(e => e.SourceType).HasColumnName("source_type").HasMaxLength(CatalogColumns.SourceTypeLength);
        builder.Property(e => e.SourceLocator).HasColumnName("source_locator").HasMaxLength(CatalogColumns.LocatorLength);
        builder.Property(e => e.FirstSeen).HasColumnName("first_seen");
        builder.Property(e => e.LastSeen).HasColumnName("last_seen");
        builder.Property(e => e.Confidence).HasColumnName("confidence");
        builder.Property(e => e.Confirmed).HasColumnName("confirmed");
        builder.Property(e => e.IsFact).HasColumnName("is_fact");
        builder.HasIndex(e => new { e.OrganizationId, e.Kind, e.EntityRef }).HasDatabaseName("ix_software_entity_kind");
        builder.HasIndex(e => new { e.OrganizationId, e.LastSeen }).HasDatabaseName("ix_software_entity_last_seen");
    }
}

internal sealed class SoftwareEdgeRowConfiguration : IEntityTypeConfiguration<SoftwareEdgeRow>
{
    public void Configure(EntityTypeBuilder<SoftwareEdgeRow> builder)
    {
        builder.ToTable("software_edge");
        // The primary key serves source → target traversal; ix_software_edge_reverse serves target → source.
        builder.HasKey(e => new { e.OrganizationId, e.SourceRef, e.Relation, e.TargetRef });
        builder.Property(e => e.OrganizationId).HasColumnName("organization_id").HasMaxLength(CatalogColumns.OrganizationLength);
        builder.Property(e => e.SourceRef).HasColumnName("source_ref").HasMaxLength(CatalogColumns.RefLength);
        builder.Property(e => e.Relation).HasColumnName("relation").HasMaxLength(CatalogColumns.RelationLength);
        builder.Property(e => e.TargetRef).HasColumnName("target_ref").HasMaxLength(CatalogColumns.RefLength);
        builder.Property(e => e.SourceType).HasColumnName("source_type").HasMaxLength(CatalogColumns.SourceTypeLength);
        builder.Property(e => e.SourceLocator).HasColumnName("source_locator").HasMaxLength(CatalogColumns.LocatorLength);
        builder.Property(e => e.FirstSeen).HasColumnName("first_seen");
        builder.Property(e => e.LastSeen).HasColumnName("last_seen");
        builder.Property(e => e.Confidence).HasColumnName("confidence");
        builder.Property(e => e.Confirmed).HasColumnName("confirmed");
        builder.Property(e => e.IsFact).HasColumnName("is_fact");
        builder.Property(e => e.ConfirmedBy).HasColumnName("confirmed_by").HasMaxLength(256);
        builder.Property(e => e.ConfirmedAt).HasColumnName("confirmed_at");
        builder.HasIndex(e => new { e.OrganizationId, e.TargetRef, e.Relation, e.SourceRef }).HasDatabaseName("ix_software_edge_reverse");
        builder.HasIndex(e => new { e.OrganizationId, e.Relation }).HasDatabaseName("ix_software_edge_relation");
        builder.HasIndex(e => e.OrganizationId).HasDatabaseName("ix_software_edge_unconfirmed").HasFilter("NOT is_fact");
    }
}

internal sealed class SoftwareEntityObservationRowConfiguration : IEntityTypeConfiguration<SoftwareEntityObservationRow>
{
    public void Configure(EntityTypeBuilder<SoftwareEntityObservationRow> builder)
    {
        builder.ToTable("software_entity_observation");
        builder.HasKey(e => new { e.OrganizationId, e.EntityRef, e.SourceLocator });
        builder.Property(e => e.OrganizationId).HasColumnName("organization_id").HasMaxLength(CatalogColumns.OrganizationLength);
        builder.Property(e => e.EntityRef).HasColumnName("entity_ref").HasMaxLength(CatalogColumns.RefLength);
        builder.Property(e => e.SourceLocator).HasColumnName("source_locator").HasMaxLength(CatalogColumns.LocatorLength);
        builder.Property(e => e.SourceType).HasColumnName("source_type").HasMaxLength(CatalogColumns.SourceTypeLength);
        builder.Property(e => e.Title).HasColumnName("title").HasMaxLength(512);
        builder.Property(e => e.Description).HasColumnName("description");
        builder.Property(e => e.Technologies).HasColumnName("technologies").HasColumnType("text[]");
        builder.Property(e => e.Attributes).HasColumnName("attributes").HasColumnType("jsonb");
        builder.Property(e => e.FirstSeen).HasColumnName("first_seen");
        builder.Property(e => e.LastSeen).HasColumnName("last_seen");
        builder.Property(e => e.Confidence).HasColumnName("confidence");
        builder.Property(e => e.Confirmed).HasColumnName("confirmed");
        builder.HasIndex(e => new { e.OrganizationId, e.SourceLocator }).HasDatabaseName("ix_software_entity_observation_locator");
    }
}

internal sealed class SoftwareEdgeObservationRowConfiguration : IEntityTypeConfiguration<SoftwareEdgeObservationRow>
{
    public void Configure(EntityTypeBuilder<SoftwareEdgeObservationRow> builder)
    {
        builder.ToTable("software_edge_observation");
        builder.HasKey(e => new { e.OrganizationId, e.SourceRef, e.Relation, e.TargetRef, e.SourceLocator });
        builder.Property(e => e.OrganizationId).HasColumnName("organization_id").HasMaxLength(CatalogColumns.OrganizationLength);
        builder.Property(e => e.SourceRef).HasColumnName("source_ref").HasMaxLength(CatalogColumns.RefLength);
        builder.Property(e => e.Relation).HasColumnName("relation").HasMaxLength(CatalogColumns.RelationLength);
        builder.Property(e => e.TargetRef).HasColumnName("target_ref").HasMaxLength(CatalogColumns.RefLength);
        builder.Property(e => e.SourceLocator).HasColumnName("source_locator").HasMaxLength(CatalogColumns.LocatorLength);
        builder.Property(e => e.SourceType).HasColumnName("source_type").HasMaxLength(CatalogColumns.SourceTypeLength);
        builder.Property(e => e.FirstSeen).HasColumnName("first_seen");
        builder.Property(e => e.LastSeen).HasColumnName("last_seen");
        builder.Property(e => e.Confidence).HasColumnName("confidence");
        builder.Property(e => e.Confirmed).HasColumnName("confirmed");
        builder.HasIndex(e => new { e.OrganizationId, e.SourceLocator }).HasDatabaseName("ix_software_edge_observation_locator");
    }
}

internal sealed class RepositoryBindingRowConfiguration : IEntityTypeConfiguration<RepositoryBindingRow>
{
    public void Configure(EntityTypeBuilder<RepositoryBindingRow> builder)
    {
        builder.ToTable("repository_binding");
        // The same alias may legitimately point at several repositories (two repos named "gateway" in
        // different namespaces); resolution then reports the alias as ambiguous instead of picking one.
        builder.HasKey(e => new { e.OrganizationId, e.Alias, e.AliasType, e.RepositoryRef });
        builder.Property(e => e.OrganizationId).HasColumnName("organization_id").HasMaxLength(CatalogColumns.OrganizationLength);
        builder.Property(e => e.Alias).HasColumnName("alias").HasMaxLength(CatalogColumns.LocatorLength);
        builder.Property(e => e.AliasType).HasColumnName("alias_type").HasMaxLength(16);
        builder.Property(e => e.RepositoryRef).HasColumnName("repository_ref").HasMaxLength(CatalogColumns.RefLength);
        builder.HasIndex(e => new { e.OrganizationId, e.RepositoryRef }).HasDatabaseName("ix_repository_binding_repository");
    }
}

internal sealed class CatalogStateRowConfiguration : IEntityTypeConfiguration<CatalogStateRow>
{
    public void Configure(EntityTypeBuilder<CatalogStateRow> builder)
    {
        builder.ToTable("catalog_state");
        builder.HasKey(e => e.OrganizationId);
        builder.Property(e => e.OrganizationId).HasColumnName("organization_id").HasMaxLength(CatalogColumns.OrganizationLength);
        builder.Property(e => e.Version).HasColumnName("version");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");
    }
}
