using Axiom.Application.Catalog;
using Axiom.Application.Evaluation;
using Axiom.Application.Governance;
using Axiom.Application.Policy;
using Axiom.Infrastructure.Persistence;
using Axiom.Workers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Axiom.IntegrationTests.Support;

/// <summary>The hosts must resolve the whole object graph: a missing registration fails here, not at first request.</summary>
[Collection(PostgresTests.Name)]
public sealed class CompositionTests(PostgresFixture postgres)
{
    private static readonly Type[] Services =
    [
        typeof(EvaluationPipeline), typeof(PreflightService), typeof(DiffEvaluationService), typeof(IScmDiffSource), typeof(PolicyEngine), typeof(IPolicyRuleCatalog),
        typeof(IGovernanceSynchronizer), typeof(IGovernanceQueries), typeof(IGovernanceSnapshotProvider),
        typeof(ISystemGraph), typeof(ICatalogWriter), typeof(AxiomDbContext),
    ];

    [Fact]
    public async Task The_api_host_resolves_every_module_and_reports_ready()
    {
        // UseSetting, not ConfigureAppConfiguration: minimal hosting reads configuration before late callbacks run.
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseSetting("ConnectionStrings:Axiom", postgres.ConnectionString);
            host.UseSetting("Axiom:Auth:Authority", "https://issuer.example.test");
            host.UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = true;
                options.ValidateOnBuild = true;
            });
        });

        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();

        foreach (var service in Services)
        {
            Assert.NotNull(scope.ServiceProvider.GetRequiredService(service));
        }

        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync(new Uri("/health/ready", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public void The_worker_host_resolves_every_module()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Axiom"] = postgres.ConnectionString })
            .Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration).AddLogging();
        services.AddAxiomWorkers(configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        foreach (var service in Services)
        {
            Assert.NotNull(scope.ServiceProvider.GetRequiredService(service));
        }
    }
}
