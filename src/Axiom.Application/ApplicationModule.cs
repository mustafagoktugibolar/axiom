using Axiom.Application.Evaluation;
using Axiom.Application.Exceptions;
using Axiom.Application.Query;
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
        services.AddScoped<PullRequestStatusReporter>();
        services.AddScoped<ExceptionRequestService>();
        services.AddScoped<GovernanceReadService>();
        services.AddScoped<ImpactService>();
        services.AddScoped<ReceiptService>();
        services.AddScoped<ContextService>();
        services.AddScoped<FindingExplanationService>();
        services.AddScoped<ReviewService>();
        return services;
    }
}
