using Axiom.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Axiom.IntegrationTests.Support;

/// <summary>
/// One PostgreSQL (with pgvector) container shared by all integration tests in the "postgres"
/// collection. Tests isolate themselves by using a unique organization ID, mirroring tenant isolation.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("axiom")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public AxiomDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AxiomDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.UseVector().MigrationsHistoryTable("__ef_migrations", AxiomDbContext.Schema))
            .Options);

    /// <summary>A fresh organization ID so tests never observe each other's rows.</summary>
    public static string NewOrganization() => "org-" + Guid.NewGuid().ToString("N")[..12];

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class PostgresTests : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
