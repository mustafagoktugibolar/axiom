using Axiom.Application;
using Axiom.Application.Policy;
using Axiom.Infrastructure;
using Axiom.Infrastructure.Catalog;
using Axiom.Infrastructure.Governance;
using Axiom.Infrastructure.Scm;

namespace Axiom.Workers;

public static class WorkerComposition
{
    /// <summary>Everything a worker host needs: use cases, persistence, governance, the System Graph and the policy engine.</summary>
    public static IServiceCollection AddAxiomWorkers(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddAxiomApplication();
        services.AddAxiomInfrastructure(configuration, "axiom-workers");
        services.AddGovernanceInfrastructure(configuration);
        services.AddCatalogInfrastructure(configuration);
        services.AddScmInfrastructure(configuration);
        services.AddPolicyEngine();
        return services;
    }
}
