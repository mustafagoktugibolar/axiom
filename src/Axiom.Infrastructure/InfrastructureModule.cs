using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Application.Exceptions;
using Axiom.Application.Review;
using Axiom.Infrastructure.Audit;
using Axiom.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Axiom.Infrastructure;

public static class InfrastructureModule
{
    public const string ConnectionStringName = "Axiom";

    /// <summary>Persistence, outbox, audit stores and telemetry shared by every host.</summary>
    public static IServiceCollection AddAxiomInfrastructure(this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        services.AddDbContext<AxiomDbContext>(options => options.UseNpgsql(connectionString, npgsql =>
        {
            npgsql.UseVector();
            npgsql.EnableRetryOnFailure(3);
            npgsql.MigrationsHistoryTable("__ef_migrations", AxiomDbContext.Schema);
        }));

        services.AddMemoryCache();
        services.AddScoped<IEventOutbox, EfEventOutbox>();
        services.AddScoped<IEvaluationStore, EfEvaluationStore>();
        services.AddScoped<IReviewStore, EfReviewStore>();
        services.AddScoped<IExceptionRequestStore, EfExceptionRequestStore>();

        services.AddHealthChecks().AddDbContextCheck<AxiomDbContext>("postgresql", tags: ["ready"]);

        // The OTLP exporter reads OTEL_EXPORTER_OTLP_* from the environment; with no endpoint set it stays idle.
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddSource(AxiomTelemetry.Name)
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddMeter(AxiomTelemetry.Name)
                .AddRuntimeInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter());

        return services;
    }
}
