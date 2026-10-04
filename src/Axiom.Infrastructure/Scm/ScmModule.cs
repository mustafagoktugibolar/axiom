using Axiom.Application.Evaluation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Axiom.Infrastructure.Scm;

public static class ScmModule
{
    /// <summary>Registers the Git-backed diff source. Needs the System Graph (<c>AddCatalogInfrastructure</c>) to locate repositories.</summary>
    public static IServiceCollection AddScmInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ScmOptions>().Bind(configuration.GetSection(ScmOptions.SectionName));
        services.TryAddScoped<IScmDiffSource, GitScmDiffSource>();
        return services;
    }
}
