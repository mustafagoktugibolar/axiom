using Axiom.Application.Policy;
using Axiom.Application.Policy.Custom;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Axiom.Infrastructure.Policy;

public static class CustomRunnerModule
{
    /// <summary>
    /// Registers the custom rule runner. Off unless <c>Axiom:Policy:CustomRunner:Enabled</c> is true. When on, the
    /// catalog is the built-in catalog plus the configured <c>custom/*</c> rules. Call this before
    /// <c>AddPolicyEngine()</c>, which keeps an already registered <see cref="IPolicyRuleCatalog"/>.
    /// </summary>
    public static IServiceCollection AddCustomPolicyRunner(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(CustomRunnerOptions.SectionName);
        services.AddOptions<CustomRunnerOptions>().Bind(section);
        var options = section.Get<CustomRunnerOptions>() ?? new CustomRunnerOptions();
        if (!options.Enabled)
        {
            return services;
        }

        var names = options.Rules.Select(r => r.Name).ToArray();
        if (names.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 64 || !n.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
        {
            throw new InvalidOperationException(
                "Axiom:Policy:CustomRunner:Rules: every rule needs a unique name of letters, digits, '-', '_' or '.' (at most 64 characters).");
        }

        services.AddSingleton<ICustomRunnerProcess>(sp =>
            new ProcessCustomRunnerProcess(options.Command, options.Arguments));
        services.AddSingleton<IPolicyRuleCatalog>(sp =>
            new CompositePolicyRuleCatalog(
                PolicyRuleCatalog.BuiltIn,
                options.Rules.Select(r => new CustomRuleExecutor(
                    new CustomRuleDefinition(r.Name, r.Version, r.Description, r.Spec),
                    sp.GetRequiredService<ICustomRunnerProcess>(),
                    options))));
        return services;
    }
}
