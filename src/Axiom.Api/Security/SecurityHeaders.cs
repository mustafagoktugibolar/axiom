namespace Axiom.Api.Security;

/// <summary>
/// Browser hardening headers for everything the API serves, including the portal SPA. The portal is a
/// same-origin bundle with no inline script, so scripts are limited to 'self'. Style attributes (Vue
/// bindings) need style-src-attr; stylesheets themselves stay 'self'. connect-src additionally allows
/// the OIDC authority because the SPA runs the authorization-code + PKCE exchange from the browser.
/// </summary>
internal static class SecurityHeaders
{
    public static string ContentSecurityPolicy(string? oidcAuthority)
    {
        var connect = "'self'";
        if (Uri.TryCreate(oidcAuthority, UriKind.Absolute, out var authority)
            && (authority.Scheme == Uri.UriSchemeHttps || authority.IsLoopback))
        {
            connect += " " + authority.GetLeftPart(UriPartial.Authority);
        }

        return "default-src 'none'; "
            + "script-src 'self'; "
            + "style-src 'self'; style-src-attr 'unsafe-inline'; "
            + "img-src 'self' data:; font-src 'self'; "
            + $"connect-src {connect}; "
            + "base-uri 'none'; form-action 'self'; frame-ancestors 'none'; object-src 'none'";
    }

    public static void Apply(IHeaderDictionary headers, string? oidcAuthority)
    {
        headers["Content-Security-Policy"] = ContentSecurityPolicy(oidcAuthority);
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
    }

    public static IApplicationBuilder UseAxiomSecurityHeaders(this IApplicationBuilder app, string? oidcAuthority) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                Apply(context.Response.Headers, oidcAuthority);
                return Task.CompletedTask;
            });
            return next();
        });
}
