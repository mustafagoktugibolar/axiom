using Axiom.Application.Candidates;
using Axiom.Application.Coverage;
using Axiom.Application.Evaluation;
using Axiom.Application.Exceptions;
using Axiom.Application.Governance;
using Axiom.Application.Query;
using Axiom.Application.Review;
using Axiom.Application.Semantic;
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
        services.AddOptions<SemanticOptions>();
        services.AddScoped<SemanticAnalyzer>();
        services.AddOptions<SemanticCalibrationOptions>();
        services.AddScoped<SemanticMetricsService>();
        services.AddScoped<DiffEvaluationService>();
        services.AddScoped<PullRequestStatusReporter>();
        services.AddScoped<ExceptionRequestService>();
        services.AddScoped<CandidateService>();
        services.AddScoped<SemanticCandidateExtractor>();
        services.AddOptions<CandidateClusteringOptions>();
        services.AddScoped<CandidateClusteringService>();
        services.AddScoped<CandidateMergeReconciler>();
        services.AddScoped<AdrImportService>();
        services.AddScoped<GovernanceCoverageService>();
        services.AddScoped<GovernanceReadService>();
        services.AddOptions<GovernanceHealthOptions>();
        services.AddScoped<GovernanceHealthService>();
        services.AddScoped<ExceptionExpiryNotifier>();
        services.AddScoped<GovernanceGraphService>();
        services.AddScoped<ImpactService>();
        services.AddScoped<ReceiptService>();
        services.AddScoped<ContextService>();
        services.AddScoped<FindingExplanationService>();
        services.AddScoped<ReviewService>();
        return services;
    }
}
