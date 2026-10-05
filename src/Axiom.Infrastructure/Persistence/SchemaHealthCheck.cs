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
            if (!await db.Database.CanConnectAsync(cancellationToken))
            {
                return HealthCheckResult.Unhealthy("The database is not reachable.");
            }

            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            return pending.Count == 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"{pending.Count} migration(s) pending ({pending[0]}...). Run the migration job.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The database schema could not be checked.", ex);
        }
    }
}
