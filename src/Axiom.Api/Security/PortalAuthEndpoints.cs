using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Axiom.Application.Common;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Axiom.Api.Security;

public sealed record DevLoginBody(string? Name, string? Organization, string[]? Roles);

/// <summary>
/// Sign-in support for the portal. <c>/v1/auth/config</c> tells the SPA how to sign in: against the
/// configured OIDC authority (production) or through <c>/v1/auth/dev-login</c> (Development only, where
/// the API itself holds the signing key). The dev endpoint does not exist outside Development.
/// </summary>
internal static class PortalAuthEndpoints
{
    private static readonly TimeSpan DevTokenLifetime = TimeSpan.FromHours(8);

    public static IEndpointRouteBuilder MapPortalAuth(this IEndpointRouteBuilder routes, IHostEnvironment environment)
    {
        var auth = routes.MapGroup("/v1/auth").WithTags("Auth").AllowAnonymous();

        auth.MapGet("/config", (IOptions<AxiomAuthOptions> options) =>
        {
            var o = options.Value;
            return string.IsNullOrEmpty(o.DevelopmentSigningKey)
                ? (object)new { mode = "oidc", authority = o.Authority, clientId = o.PortalClientId, scopes = o.PortalScopes }
                : new { mode = "dev" };
        }).WithName("authConfig");

        if (environment.IsDevelopment())
        {
            auth.MapPost("/dev-login", (DevLoginBody? body, IOptions<AxiomAuthOptions> options) =>
            {
                var o = options.Value;
                if (string.IsNullOrEmpty(o.DevelopmentSigningKey))
                {
                    return Results.NotFound();
                }

                var roles = body?.Roles is { Length: > 0 } requested ? requested : Enum.GetNames<Role>();
                var name = string.IsNullOrWhiteSpace(body?.Name) ? "dev-user" : body.Name.Trim();
                var claims = new List<Claim>
                {
                    new("sub", name), new("name", name),
                    new(o.OrganizationClaim, string.IsNullOrWhiteSpace(body?.Organization) ? "dev" : body.Organization.Trim()),
                };
                claims.AddRange(roles.Select(r => new Claim(o.RolesClaim, r)));
                claims.AddRange(o.DevelopmentGroups.Select(g => new Claim(o.TeamsClaim, g)));
                var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.DevelopmentSigningKey));
                var token = new JwtSecurityToken(
                    o.DevelopmentIssuer, o.Audience, claims, expires: DateTime.UtcNow.Add(DevTokenLifetime),
                    signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
                return Results.Ok(new { accessToken = new JwtSecurityTokenHandler().WriteToken(token), expiresIn = (int)DevTokenLifetime.TotalSeconds });
            }).WithName("devLogin");
        }

        return routes;
    }
}
