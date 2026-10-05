using LibGit2Sharp;
using Microsoft.AspNetCore.Authorization;

namespace Axiom.Api;

/// <summary>
/// Local-trial helper: tells the portal which commits of the bundled demo code repository to validate, so
/// the "Get started" page can run a pull-request check without anyone copying SHAs. Mapped only in the
/// Development environment and only when <c>Axiom:Demo:RepositoryPath</c> points at a repository.
/// </summary>
internal static class DemoEndpoints
{
    public static IEndpointRouteBuilder MapDemo(this IEndpointRouteBuilder routes, IHostEnvironment environment, IConfiguration configuration)
    {
        var path = configuration["Axiom:Demo:RepositoryPath"];
        if (!environment.IsDevelopment() || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return routes;
        }

        var name = configuration["Axiom:Demo:RepositoryName"] ?? "demo-gateway";
        routes.MapGet("/v1/demo", [AllowAnonymous] () =>
        {
            using var repository = new Repository(path);
            var main = repository.Branches["main"]?.Tip;
            var feature = repository.Branches["feature/role-routing"]?.Tip;
            return main is null || feature is null
                ? Results.NotFound()
                : Results.Ok(new { repository = name, baseSha = main.Sha, headSha = feature.Sha, title = feature.MessageShort });
        }).WithName("demoInfo");
        return routes;
    }
}
