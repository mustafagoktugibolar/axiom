using Axiom.Application.Semantic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Axiom.Infrastructure.Semantic;

public static class SemanticModule
{
    public const string HeuristicProvider = "Heuristic";
    public const string AnthropicProvider = "Anthropic";

    /// <summary>
    /// Registers the configured semantic provider. Semantic analysis is off unless <c>Axiom:Semantic:Enabled</c>
    /// is true. A misconfiguration fails at startup instead of on the first evaluation, because a per-request
    /// failure would only show up as degraded coverage that nobody reads.
    /// </summary>
    public static IServiceCollection AddSemanticInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SemanticOptions.SectionName);
        services.AddOptions<SemanticOptions>().Bind(section);
        services.AddOptions<AnthropicOptions>().Bind(configuration.GetSection(AnthropicOptions.SectionName));

        var semantic = section.Get<SemanticOptions>() ?? new SemanticOptions();
        if (!semantic.Enabled)
        {
            return services;
        }

        if (string.Equals(semantic.Provider, HeuristicProvider, StringComparison.OrdinalIgnoreCase))
        {
            services.TryAddSingleton<ISemanticProvider, HeuristicSemanticProvider>();
        }
        else if (string.Equals(semantic.Provider, AnthropicProvider, StringComparison.OrdinalIgnoreCase))
        {
            var anthropic = configuration.GetSection(AnthropicOptions.SectionName).Get<AnthropicOptions>();
            if (string.IsNullOrWhiteSpace(anthropic?.ApiKey))
            {
                throw new InvalidOperationException("Axiom:Semantic:Provider is Anthropic but Axiom:Semantic:Anthropic:ApiKey is not set.");
            }

            services.AddHttpClient(AnthropicSemanticProvider.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(60))
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
            services.TryAddScoped<ISemanticProvider, AnthropicSemanticProvider>();
        }
        else
        {
            throw new InvalidOperationException($"Axiom:Semantic:Provider '{semantic.Provider}' is not supported (use {HeuristicProvider} or {AnthropicProvider}).");
        }

        return services;
    }
}
