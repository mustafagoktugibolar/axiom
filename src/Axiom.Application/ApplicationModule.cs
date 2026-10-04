using Axiom.Application.Evaluation;
using Axiom.Application.Review;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Axiom.Application;

public static class ApplicationModule
{
    /// <summary>Registers the use cases shared by REST, MCP, the CLI and workers.</summary>
    public static IServiceCollection AddAxiomApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<ChangeScopeResolver>();
        services.AddScoped<EvaluationPipeline>();
        services.AddScoped<PreflightService>();
        services.AddScoped<DesignValidationService>();
        services.AddScoped<DiffEvaluationService>();
        services.AddScoped<ReviewService>();
        return services;
    }
}
