using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Infrastructure.Catalog.Backstage;
using Axiom.Infrastructure.Catalog.Manifests;
using Axiom.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Axiom.Infrastructure.Catalog;

/// <summary>Composition of the System Graph (Catalog) module.</summary>
public static class CatalogModule
{
    /// <summary>
    /// Registers the System Graph read and write sides, the manifest parser and the impact analysis use
    /// case. The Backstage adapter is registered only when <c>Axiom:Catalog:Backstage:BaseUrl</c> is set.
    /// Expects <see cref="AxiomDbContext"/> to be registered by the host.
    /// </summary>
    public static IServiceCollection AddCatalogInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IEventOutbox, EfEventOutbox>();
        services.AddOptions<CatalogOptions>().Bind(configuration.GetSection(CatalogOptions.SectionName));
        services.AddScoped<ISystemGraph, PostgresSystemGraph>();
        services.AddScoped<ICatalogWriter, CatalogWriter>();
        services.AddSingleton<ICatalogManifestParser, CatalogManifestParser>();
        services.AddScoped<ImpactAnalyzer>();

        var backstage = configuration.GetSection(BackstageCatalogOptions.SectionName);
        if (!string.IsNullOrWhiteSpace(backstage[nameof(BackstageCatalogOptions.BaseUrl)]))
        {
            services.AddOptions<BackstageCatalogOptions>()
                .Bind(backstage)
                .Validate(o => BackstageCatalogOptions.TryValidateBaseUrl(o.BaseUrl, out _, out _),
                    "Axiom:Catalog:Backstage:BaseUrl must be an absolute https URL (http only for loopback) without credentials, query or fragment.")
                .Validate(o => o.PageSize > 0 && o.MaxEntities > 0, "Axiom:Catalog:Backstage PageSize and MaxEntities must be positive.")
                .ValidateOnStart();

            // Redirects are not followed: a validated base URL must not be able to bounce the request elsewhere.
            services.AddHttpClient<IBackstageCatalogClient, BackstageCatalogClient>(client => client.Timeout = TimeSpan.FromSeconds(100))
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
                .AddStandardResilienceHandler();
        }

        return services;
    }
}
