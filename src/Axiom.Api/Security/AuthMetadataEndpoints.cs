using Axiom.Api.Security;
using Microsoft.Extensions.Options;

namespace Axiom.Api;

/// <summary>
/// OAuth 2.0 Protected Resource Metadata (RFC 9728). MCP clients and workload identities discover from
/// it which authorization server issues tokens for Axiom and which scopes mean what.
/// </summary>
internal static class AuthMetadataEndpoints
{
    public const string Path = "/.well-known/oauth-protected-resource";

    private static readonly string[] Scopes = ["axiom.read", "axiom.evaluate", "axiom.exception.request", "axiom.audit.read"];
    private static readonly string[] BearerMethods = ["header"];

    public static string MetadataUrl(HttpContext context) => BaseUrl(context) + Path;

    public static IEndpointRouteBuilder MapAuthMetadata(this IEndpointRouteBuilder routes)
    {
        routes.MapGet(Path, (HttpContext context, IOptions<AxiomAuthOptions> auth) => new
        {
            resource = BaseUrl(context) + "/mcp",
            authorization_servers = string.IsNullOrWhiteSpace(auth.Value.Authority) ? Array.Empty<string>() : [auth.Value.Authority],
            scopes_supported = Scopes,
            bearer_methods_supported = BearerMethods,
            resource_name = "Axiom engineering governance",
        }).AllowAnonymous().WithName("protectedResourceMetadata");
        return routes;
    }

    /// <summary>The public base URL: configured when Axiom runs behind a proxy, otherwise taken from the request.</summary>
    private static string BaseUrl(HttpContext context)
    {
        var configured = context.RequestServices.GetRequiredService<IConfiguration>()["Axiom:PublicBaseUrl"];
        return string.IsNullOrWhiteSpace(configured) ? $"{context.Request.Scheme}://{context.Request.Host}" : configured.TrimEnd('/');
    }
}
