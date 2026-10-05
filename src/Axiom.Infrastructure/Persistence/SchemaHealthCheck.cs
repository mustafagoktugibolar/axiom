using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Axiom.Infrastructure.Persistence;

/// <summary>
/// Readiness: the database is reachable AND its schema is fully migrated. Reachability alone reports a
/// pod healthy while every request would fail on a missing table (the state before the migration job
/// has run, or after it failed).
/// </summary>
public sealed class SchemaHealthCheck(AxiomDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.IsFullyMigratedAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The database is unreachable or its schema is not fully migrated (run the migration job).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The database schema could not be checked.", ex);
        }
    }
}
