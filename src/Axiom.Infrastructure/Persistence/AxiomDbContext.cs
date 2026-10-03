using Microsoft.EntityFrameworkCore;

namespace Axiom.Infrastructure.Persistence;

/// <summary>
/// The single runtime database: projections, topology, audit metadata and vectors. It is never the
/// source of truth for governance content (ADR-0001); every governance table is rebuildable from Git.
/// Each module contributes its own <see cref="IEntityTypeConfiguration{TEntity}"/> classes in this
/// assembly and reaches its tables through <c>Set&lt;T&gt;()</c>; modules do not read each other's rows.
/// </summary>
public sealed class AxiomDbContext(DbContextOptions<AxiomDbContext> options) : DbContext(options)
{
    public const string Schema = "axiom";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AxiomDbContext).Assembly);
    }
}
