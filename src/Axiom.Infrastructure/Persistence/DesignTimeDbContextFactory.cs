using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Axiom.Infrastructure.Persistence;

/// <summary>Lets <c>dotnet ef</c> build the model without a running host. The connection is never opened to generate migrations.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AxiomDbContext>
{
    public AxiomDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AxiomDbContext>()
            .UseNpgsql(
                Environment.GetEnvironmentVariable("AXIOM_DESIGN_CONNECTION") ?? "Host=localhost;Database=axiom;Username=axiom",
                npgsql => npgsql.UseVector())
            .Options);
}
