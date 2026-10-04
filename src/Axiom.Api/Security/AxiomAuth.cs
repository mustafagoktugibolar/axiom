using System.Collections.Immutable;
using System.Security.Claims;
using System.Text;
using Axiom.Application.Common;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Axiom.Api.Security;

/// <summary>OIDC / OAuth2 settings (section <c>Axiom:Auth</c>). Tokens come from the enterprise identity provider.</summary>
public sealed class AxiomAuthOptions
{
    public const string Section = "Axiom:Auth";

    /// <summary>OIDC authority (issuer). Signing keys are discovered from its metadata.</summary>
    public string? Authority { get; set; }

    public string Audience { get; set; } = "axiom";

    public string OrganizationClaim { get; set; } = "org";

    public string RolesClaim { get; set; } = "roles";

    public string TeamsClaim { get; set; } = "groups";

    /// <summary>
    /// Symmetric key for locally issued tokens. Honoured only in the Development environment; startup
    /// fails if it is set anywhere else.
    /// </summary>
    public string? DevelopmentSigningKey { get; set; }

    public string DevelopmentIssuer { get; set; } = "https://axiom.localhost";
}

public static class AxiomAuth
{
    /// <summary>OAuth scopes of rest-api.yaml mapped to the roles they stand for (workload identities).</summary>
    private static readonly ImmutableDictionary<string, Role> ScopeRoles = new Dictionary<string, Role>(StringComparer.Ordinal)
    {
        ["axiom.read"] = Role.Reader,
        ["axiom.evaluate"] = Role.Contributor,
        ["axiom.exception.request"] = Role.Contributor,
        ["axiom.audit.read"] = Role.Auditor,
    }.ToImmutableDictionary();

    private static readonly ImmutableDictionary<string, Role> RoleNames = Enum.GetValues<Role>()
        .SelectMany(role => new[] { (Name: role.ToString(), role), (Name: ToKebab(role.ToString()), role), (Name: "axiom." + ToKebab(role.ToString()), role) })
        .ToImmutableDictionary(x => x.Name, x => x.role, StringComparer.OrdinalIgnoreCase);

    public static IServiceCollection AddAxiomAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var options = configuration.GetSection(AxiomAuthOptions.Section).Get<AxiomAuthOptions>() ?? new AxiomAuthOptions();
        services.Configure<AxiomAuthOptions>(configuration.GetSection(AxiomAuthOptions.Section));

        var useDevelopmentKey = !string.IsNullOrEmpty(options.DevelopmentSigningKey);
        if (useDevelopmentKey && !environment.IsDevelopment())
        {
            throw new InvalidOperationException("Axiom:Auth:DevelopmentSigningKey may only be configured in the Development environment.");
        }

        if (!useDevelopmentKey && string.IsNullOrWhiteSpace(options.Authority))
        {
            throw new InvalidOperationException("Axiom:Auth:Authority must be configured with the OIDC issuer.");
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
        {
            jwt.MapInboundClaims = false;
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidAudience = options.Audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                NameClaimType = "sub",
            };

            if (useDevelopmentKey)
            {
                jwt.TokenValidationParameters.ValidIssuer = options.DevelopmentIssuer;
                jwt.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.DevelopmentSigningKey!));
            }
            else
            {
                jwt.Authority = options.Authority;
                jwt.RequireHttpsMetadata = true;
            }
        });

        services.AddAuthorizationBuilder().SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddHttpContextAccessor();
        services.AddScoped(provider =>
        {
            var http = provider.GetRequiredService<IHttpContextAccessor>().HttpContext
                ?? throw new InvalidOperationException("No HTTP request is in progress.");
            var auth = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AxiomAuthOptions>>().Value;
            return ToPrincipal(http.User, auth);
        });
        return services;
    }

    /// <summary>
    /// Builds the application principal from validated token claims. The organization always comes from
    /// the token; a token without one cannot act (R23).
    /// </summary>
    public static AxiomPrincipal ToPrincipal(ClaimsPrincipal user, AxiomAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(options);

        var subject = user.FindFirstValue("sub") ?? user.FindFirstValue("client_id");
        var organization = user.FindFirstValue(options.OrganizationClaim);
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(organization))
        {
            throw AxiomException.Forbidden("The access token carries no subject or organization.");
        }

        var roles = Values(user, options.RolesClaim).Select(r => RoleNames.TryGetValue(r, out var role) ? role : (Role?)null).OfType<Role>()
            .Concat(Values(user, "scope").Concat(Values(user, "scp")).Select(s => ScopeRoles.TryGetValue(s, out var role) ? role : (Role?)null).OfType<Role>())
            .ToImmutableHashSet();

        return new AxiomPrincipal(
            subject,
            user.FindFirstValue("name"),
            organization.Trim(),
            roles,
            Values(user, options.TeamsClaim).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Claims may arrive as repeated claims or as one space-separated value (OAuth <c>scope</c>).</summary>
    private static IEnumerable<string> Values(ClaimsPrincipal user, string type) =>
        user.FindAll(type).SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string ToKebab(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) && i > 0 ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
