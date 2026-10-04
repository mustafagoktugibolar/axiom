using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Axiom.Application.Policy;

public static class PolicyModule
{
    /// <summary>
    /// Registers the deterministic policy engine with the trusted built-in rule catalog. Both are
    /// stateless singletons; a host may register its own <see cref="IPolicyRuleCatalog"/> beforehand.
    /// </summary>
    public static IServiceCollection AddPolicyEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IPolicyRuleCatalog>(PolicyRuleCatalog.BuiltIn);
        services.TryAddSingleton<PolicyEngine>();
        return services;
    }
}
