using Axiom.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Axiom.Workers;

/// <summary>
/// Workers start alongside the migration job. Until the schema is fully migrated every query would fail
/// with a noisy stack trace, so cycles wait quietly and resume on the next tick.
/// </summary>
public interface ISchemaGate
{
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);
}

internal sealed class DbSchemaGate(AxiomDbContext db) : ISchemaGate
{
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await db.IsFullyMigratedAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
