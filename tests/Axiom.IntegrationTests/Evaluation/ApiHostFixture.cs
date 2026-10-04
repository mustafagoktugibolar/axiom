using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Axiom.Application.Catalog;
using Axiom.Application.Governance;
using Axiom.Domain.Catalog;
using Axiom.IntegrationTests.Governance;
using Axiom.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Axiom.IntegrationTests.Evaluation;

/// <summary>
/// The real API host over a real PostgreSQL, with a governance repository and a code repository on
/// local disk. Requests are authenticated with locally issued tokens, exactly as a CI job would send them.
/// </summary>
internal sealed class ApiHost : IAsyncDisposable
{
    private const string SigningKey = "integration-test-signing-key-0123456789abcdef";
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "axiom-tests", "api-" + Guid.NewGuid().ToString("N"));

    public ApiHost(PostgresFixture postgres, bool allowLocalScm = true)
    {
        Organization = PostgresFixture.NewOrganization();
        Governance = new GovernanceRepo();
        Code = new GovernanceRepo();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Development");
            host.UseSetting("ConnectionStrings:Axiom", postgres.ConnectionString);
            host.UseSetting("Axiom:Auth:DevelopmentSigningKey", SigningKey);
            host.UseSetting("Axiom:Governance:AllowLocalRepositories", "true");
            host.UseSetting("Axiom:Governance:CacheDirectory", Path.Combine(_cache, "governance"));
            host.UseSetting("Axiom:Scm:AllowLocalRepositories", allowLocalScm ? "true" : "false");
            host.UseSetting("Axiom:Scm:CacheDirectory", Path.Combine(_cache, "scm"));
            host.UseSetting("Axiom:RateLimit:Burst", "1000");
        });
    }

    public string Organization { get; }

    public GovernanceRepo Governance { get; }

    public GovernanceRepo Code { get; }

    /// <summary>Publishes the current head of the governance repository as a new snapshot.</summary>
    public async Task SyncGovernanceAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var sync = await scope.ServiceProvider.GetRequiredService<IGovernanceSynchronizer>()
            .SynchronizeAsync(Governance.ConfigFor(Organization), SyncMode.Incremental, CancellationToken.None);
        Assert.Equal(SyncOutcome.Published, sync.Outcome);
    }

    /// <summary>Syncs the governance repository and registers the code repository in the System Graph.</summary>
    public async Task SeedAsync(string repositoryName = "gateway")
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var sync = await scope.ServiceProvider.GetRequiredService<IGovernanceSynchronizer>()
            .SynchronizeAsync(Governance.ConfigFor(Organization), SyncMode.Incremental, CancellationToken.None);
        Assert.Equal(SyncOutcome.Published, sync.Outcome);

        var at = DateTimeOffset.UtcNow;
        var entity = new SoftwareEntity(
            EntityRef.Parse($"repo:{repositoryName}"), repositoryName, null, [],
            ImmutableSortedDictionary<string, string>.Empty.Add(RepositoryAttributes.CloneUrls, Code.Location),
            EntityProvenance.Declared(ProvenanceSource.RepositoryManifest, "repo:" + repositoryName + "#catalog", at));
        await scope.ServiceProvider.GetRequiredService<ICatalogWriter>()
            .ImportAsync(Organization, new CatalogImport(ProvenanceSource.RepositoryManifest, "repo:" + repositoryName + "#catalog", [entity], []), CancellationToken.None);
    }

    public HttpClient Client(params string[] roles) => ClientFor("ci-bot", [], roles);

    public HttpClient ClientFor(string subject, string[] teams, params string[] roles)
    {
        var client = _factory.CreateClient();
        var handler = new JsonWebTokenHandler();
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://axiom.localhost",
            Audience = "axiom",
            Expires = DateTime.UtcNow.AddHours(1),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["groups"] = teams,
                ["org"] = Organization,
                ["roles"] = roles.Length == 0 ? new[] { "contributor" } : roles,
            },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256),
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public HttpClient AnonymousClient() => _factory.CreateClient();

    public static async Task<(int Status, JsonElement Body)> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        var json = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, json.Length == 0 ? default : JsonDocument.Parse(json).RootElement.Clone());
    }

    public static async Task<(int Status, JsonElement Body)> PostAsync(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), body);
        var json = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, json.Length == 0 ? default : JsonDocument.Parse(json).RootElement.Clone());
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        Governance.Dispose();
        Code.Dispose();
        GovernanceRepo.ForceDelete(_cache);
    }
}

internal static class GovernanceFixtures
{
    /// <summary>A block-level rule: nothing under <c>src/Generated/</c> may be changed in the gateway repository.</summary>
    public static string GeneratedCodeDecision(string id = "ARCH-100") => $"""
        schemaVersion: axiom.io/v1
        kind: Decision
        metadata:
          id: {id}
          title: Generated code is not edited by hand
          owners: [platform-architecture]
        spec:
          status: accepted
          scope:
            repositories: [gateway]
          authority:
            level: system
            exemptable: true
          enforcement:
            defaultVerdict: warn
            rules:
              - ruleId: forbidden-path-change
                mode: block
                with:
                  paths: "src/Generated/**"
          decision: Generated sources are produced by the build and never edited by hand.

        """;
}
