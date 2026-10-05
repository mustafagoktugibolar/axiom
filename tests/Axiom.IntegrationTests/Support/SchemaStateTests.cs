using Axiom.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Axiom.IntegrationTests.Support;

[Collection(PostgresTests.Name)]
public sealed class SchemaStateTests(PostgresFixture postgres)
{
    private static AxiomDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<AxiomDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.UseVector().MigrationsHistoryTable("__ef_migrations", AxiomDbContext.Schema))
            .Options);

    [Fact]
    public async Task A_migrated_database_is_ready()
    {
        await using var db = postgres.CreateContext();

        Assert.True(await db.IsFullyMigratedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_reachable_but_unmigrated_database_is_not_ready_and_does_not_throw()
    {
        var name = "empty_" + Guid.NewGuid().ToString("N")[..8];
        await using (var admin = new NpgsqlConnection(postgres.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"create database {name}", admin);
            await create.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Database = name };
        await using var db = Context(builder.ConnectionString);

        Assert.False(await db.IsFullyMigratedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_unreachable_database_is_not_ready_and_does_not_throw()
    {
        var builder = new NpgsqlConnectionStringBuilder(postgres.ConnectionString) { Host = "127.0.0.1", Port = 1, Timeout = 2 };
        await using var db = Context(builder.ConnectionString);

        Assert.False(await db.IsFullyMigratedAsync(CancellationToken.None));
    }
}
