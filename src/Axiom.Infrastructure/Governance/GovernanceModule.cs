using Axiom.Application.Common;
using Axiom.Application.Governance;
using Axiom.Infrastructure.Governance.Git;
using Axiom.Infrastructure.Governance.Parsing;
using Axiom.Infrastructure.Governance.Persistence;
using Axiom.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Axiom.Infrastructure.Governance;

public static class GovernanceModule
{
    /// <summary>
    /// Registers the governance registry: Git source, projection, synchronizer, snapshot provider,
    /// search and stale detection. The host registers <see cref="AxiomDbContext"/> itself.
    /// </summary>
    public static IServiceCollection AddGovernanceInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<GovernanceOptions>().Bind(configuration.GetSection(GovernanceOptions.SectionName));
        services.AddLogging();
        services.AddMemoryCache();
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IGovernanceRecordParser, GovernanceRecordParser>();
        services.TryAddSingleton<IGovernanceSourceCredentials, ConfiguredGovernanceSourceCredentials>();
        services.TryAddSingleton<GitGovernanceSource>();
        services.TryAddSingleton<IGovernanceSource>(provider => provider.GetRequiredService<GitGovernanceSource>());
        services.TryAddSingleton<IGovernanceCommitSource>(provider => provider.GetRequiredService<GitGovernanceSource>());
        services.TryAddSingleton<GovernanceRevisionReader>();

        services.TryAddScoped<IEventOutbox, EfEventOutbox>();
        services.TryAddScoped<IGovernanceProjectionStore, EfGovernanceProjectionStore>();
        services.TryAddScoped<IGovernanceEventWriter, EfGovernanceEventWriter>();
        services.TryAddScoped<IGovernanceSynchronizer, GovernanceSynchronizer>();
        services.TryAddScoped<IGovernanceSnapshotProvider, EfGovernanceSnapshotProvider>();
        services.TryAddScoped<IGovernanceQueries, EfGovernanceQueries>();
        services.TryAddScoped<IGovernanceSourceRegistry, EfGovernanceSourceRegistry>();
        services.TryAddScoped<StaleRecordDetector>();
        return services;
    }
}
