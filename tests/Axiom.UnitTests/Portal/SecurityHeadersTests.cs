using Axiom.Api.Security;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Axiom.UnitTests.Portal;

public sealed class SecurityHeadersTests
{
    [Fact]
    public void Csp_forbids_framing_inline_script_and_foreign_origins()
    {
        var csp = SecurityHeaders.ContentSecurityPolicy(null);
        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self';", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("object-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("base-uri 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-eval", csp, StringComparison.Ordinal);
        Assert.Contains("connect-src 'self';", csp, StringComparison.Ordinal);
    }

    [Fact]
    public void Csp_allows_only_the_https_oidc_authority_for_connect()
    {
        Assert.Contains("connect-src 'self' https://idp.example.test;",
            SecurityHeaders.ContentSecurityPolicy("https://idp.example.test/realms/axiom"), StringComparison.Ordinal);
        Assert.DoesNotContain("evil", SecurityHeaders.ContentSecurityPolicy("http://evil.example"), StringComparison.Ordinal);
        Assert.DoesNotContain("*", SecurityHeaders.ContentSecurityPolicy("not a url"), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_sets_the_hardening_headers()
    {
        var headers = new HeaderDictionary();
        SecurityHeaders.Apply(headers, null);
        Assert.Equal("nosniff", headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", headers["X-Frame-Options"]);
        Assert.Equal("no-referrer", headers["Referrer-Policy"]);
        Assert.True(headers.ContainsKey("Content-Security-Policy"));
        Assert.True(headers.ContainsKey("Permissions-Policy"));
    }
}
