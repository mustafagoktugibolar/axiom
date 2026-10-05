using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Axiom.Infrastructure.Persistence;

public static class SchemaState
{
    /// <summary>
    /// True when the database is reachable and every migration is applied. Looks for the history table first so a database that has not
    /// been migrated yet answers "no" quietly, instead of logging a failed query on every probe.
    /// </summary>
    public static async Task<bool> IsFullyMigratedAsync(this AxiomDbContext db, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);

        // Open the underlying connection directly: going through EF would log every failed attempt at Error
        // level, turning "the database is still starting" into alarming output on each retry. (The connection
        // string EF reports has its password stripped, so a new connection cannot be built from it.)
        var connection = db.Database.GetDbConnection();
        var opened = false;
        try
        {
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
                opened = true;
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException or TimeoutException)
        {
            return false;
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }

        var historyTable = await db.Database
            .SqlQuery<string>($"select coalesce(to_regclass('axiom.__ef_migrations')::text, '') as \"Value\"")
            .SingleAsync(cancellationToken);
        return historyTable.Length > 0 && !(await db.Database.GetPendingMigrationsAsync(cancellationToken)).Any();
    }
}
