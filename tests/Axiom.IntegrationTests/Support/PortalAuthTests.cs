using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Axiom.IntegrationTests.Support;

[Collection(PostgresTests.Name)]
public sealed class PortalAuthTests(PostgresFixture postgres)
{
    private WebApplicationFactory<Program> Host(string environment, params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment(environment);
            host.UseSetting("ConnectionStrings:Axiom", postgres.ConnectionString);
            foreach (var (key, value) in settings)
            {
                host.UseSetting(key, value);
            }
        });

    [Fact]
    public async Task Dev_login_issues_a_token_the_api_accepts_in_development()
    {
        await using var factory = Host("Development", ("Axiom:Auth:DevelopmentSigningKey", "portal-auth-test-signing-key-0123456789"));
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(new Uri("/v1/governance", UriKind.Relative))).StatusCode);

        var login = await client.PostAsJsonAsync(new Uri("/v1/auth/dev-login", UriKind.Relative), new { });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();

        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/v1/governance", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Outside_development_there_is_no_dev_login_and_config_advertises_oidc()
    {
        await using var factory = Host("Production", ("Axiom:Auth:Authority", "https://issuer.example.test"), ("Axiom:Auth:PortalClientId", "axiom-portal"));
        using var client = factory.CreateClient();

        var login = await client.PostAsJsonAsync(new Uri("/v1/auth/dev-login", UriKind.Relative), new { });
        Assert.NotEqual(HttpStatusCode.OK, login.StatusCode);

        var config = await client.GetFromJsonAsync<JsonElement>(new Uri("/v1/auth/config", UriKind.Relative));
        Assert.Equal("oidc", config.GetProperty("mode").GetString());
        Assert.Equal("axiom-portal", config.GetProperty("clientId").GetString());
    }
}
