using System.Net;
using System.Text;
using Axiom.Application.Catalog;
using Axiom.Domain.Catalog;
using Axiom.Infrastructure.Catalog;
using Axiom.Infrastructure.Catalog.Backstage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Axiom.UnitTests.Catalog;

public class BackstageCatalogClientTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private const string PageOne =
        """
        [
          {
            "apiVersion": "backstage.io/v1alpha1", "kind": "Component",
            "metadata": { "name": "api-gateway", "namespace": "default", "description": "Edge gateway",
                          "annotations": { "github.com/project-slug": "acme/gateway" } },
            "spec": { "type": "service", "lifecycle": "production", "owner": "platform", "system": "gui-platform" },
            "relations": [
              { "type": "ownedBy", "targetRef": "group:default/platform" },
              { "type": "partOf", "targetRef": "system:default/gui-platform" },
              { "type": "providesApi", "targetRef": "api:default/gateway-api" },
              { "type": "consumesApi", "targetRef": "api:homepage/homepage-v1" },
              { "type": "dependsOn", "targetRef": "resource:default/sessions-db" },
              { "type": "dependsOn", "targetRef": "component:default/auth-service" }
            ]
          },
          {
            "apiVersion": "backstage.io/v1alpha1", "kind": "API",
            "metadata": { "name": "gateway-api" },
            "spec": { "type": "openapi" },
            "relations": [
              { "type": "apiProvidedBy", "targetRef": "component:default/api-gateway" },
              { "type": "ownedBy", "targetRef": "user:default/jane" }
            ]
          }
        ]
        """;

    private const string PageTwo =
        """
        [
          {
            "apiVersion": "backstage.io/v1alpha1", "kind": "System",
            "metadata": { "name": "gui-platform" },
            "relations": [ { "type": "partOf", "targetRef": "domain:default/gui" }, { "type": "hasPart", "targetRef": "component:default/api-gateway" } ]
          }
        ]
        """;

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static BackstageCatalogClient Client(StubHandler handler, int pageSize = 2, string baseUrl = "https://backstage.example.com/", string? token = "s3cret") =>
        new(new HttpClient(handler), Options.Create(new BackstageCatalogOptions { BaseUrl = baseUrl, Token = token, PageSize = pageSize }));

    [Fact]
    public async Task Fetches_all_pages_and_maps_entities_and_relations_with_catalog_provenance()
    {
        var handler = new StubHandler(request => Json(request.RequestUri!.Query.Contains("offset=0", StringComparison.Ordinal) ? PageOne : PageTwo));

        var import = await Client(handler).FetchAsync("acme", ObservedAt, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("/api/catalog/entities", r.RequestUri!.AbsolutePath);
            Assert.Equal("backstage.example.com", r.RequestUri.Host);
            Assert.Contains("filter=kind=component", r.RequestUri.Query, StringComparison.Ordinal);
            Assert.Contains("limit=2", r.RequestUri.Query, StringComparison.Ordinal);
            Assert.Equal("Bearer", r.Headers.Authorization!.Scheme);
            Assert.Equal("s3cret", r.Headers.Authorization.Parameter);
        });
        Assert.Contains("offset=2", handler.Requests[1].RequestUri!.Query, StringComparison.Ordinal);

        Assert.Equal(ProvenanceSource.Catalog, import.Source);
        Assert.Equal("backstage:backstage.example.com", import.SourceLocator);
        Assert.Equal(
            ["system:gui-platform", "component:api-gateway", "repo:acme/gateway", "api:gateway-api"],
            import.Entities.Select(e => e.Ref.ToString()));
        Assert.All(import.Entities, e =>
        {
            Assert.Equal(ProvenanceSource.Catalog, e.Provenance.Source);
            Assert.Equal(ObservedAt, e.Provenance.LastSeen);
            Assert.True(e.Provenance.IsFact);
        });
        Assert.Equal("Edge gateway", import.Entities.Single(e => e.Ref.Kind == EntityKind.Component).Description);

        Assert.Equal(
            [
                "domain:gui Contains system:gui-platform",
                "system:gui-platform Contains component:api-gateway",
                "component:api-gateway Provides api:gateway-api",
                "component:api-gateway Consumes api:homepage/homepage-v1",
                "component:api-gateway DependsOn component:auth-service",
                "component:api-gateway DependsOn resource:sessions-db",
                "repo:acme/gateway Implements component:api-gateway",
                "team:platform Owns component:api-gateway",
            ],
            import.Edges.Select(e => $"{e.From} {e.Relation} {e.To}"));
    }

    [Fact]
    public async Task A_short_first_page_ends_pagination_and_no_token_sends_no_authorization()
    {
        var handler = new StubHandler(_ => Json(PageTwo));

        var import = await Client(handler, pageSize: 50, token: null).FetchAsync("acme", ObservedAt, CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Null(handler.Requests[0].Headers.Authorization);
        Assert.Single(import.Entities);
    }

    [Fact]
    public async Task A_failed_page_fails_the_fetch_instead_of_returning_a_partial_catalog()
    {
        var handler = new StubHandler(request => request.RequestUri!.Query.Contains("offset=0", StringComparison.Ordinal)
            ? Json(PageOne)
            : new HttpResponseMessage(HttpStatusCode.BadGateway));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler).FetchAsync("acme", ObservedAt, CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
    }

    [Theory]
    [InlineData("{\"items\": []}")]
    [InlineData("<html>login</html>")]
    [InlineData("[42]")]
    [InlineData("[{\"kind\": \"Component\", \"metadata\": {\"name\": 5}}]")]
    public async Task Unexpected_payloads_fail_the_fetch(string body)
    {
        var handler = new StubHandler(_ => Json(body));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).FetchAsync("acme", ObservedAt, CancellationToken.None));
    }

    [Theory]
    [InlineData("https://backstage.example.com", true)]
    [InlineData("https://backstage.example.com/backstage/", true)]
    [InlineData("http://localhost:7007", true)]
    [InlineData("http://127.0.0.1:7007", true)]
    [InlineData("http://[::1]:7007", true)]
    [InlineData("http://backstage.example.com", false)]
    [InlineData("http://169.254.169.254/latest/meta-data", false)]
    [InlineData("http://localhost.evil.example", false)]
    [InlineData("ftp://backstage.example.com", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("/relative", false)]
    [InlineData("https://user:pw@backstage.example.com", false)]
    [InlineData("https://backstage.example.com/?next=x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Base_url_must_be_https_or_loopback_http(string? baseUrl, bool valid)
    {
        Assert.Equal(valid, BackstageCatalogOptions.TryValidateBaseUrl(baseUrl, out var uri, out var error));
        Assert.Equal(valid, uri is not null);
        Assert.Equal(valid, error is null);
        if (!valid)
        {
            Assert.Throws<InvalidOperationException>(() => Client(new StubHandler(_ => Json("[]")), baseUrl: baseUrl!));
        }
    }

    [Fact]
    public async Task Requests_stay_under_a_base_url_with_a_path_prefix()
    {
        var handler = new StubHandler(_ => Json("[]"));

        await Client(handler, baseUrl: "https://tools.example.com/backstage").FetchAsync("acme", ObservedAt, CancellationToken.None);

        Assert.Equal("/backstage/api/catalog/entities", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public void The_adapter_is_registered_only_when_configured()
    {
        static ServiceCollection Build(Dictionary<string, string?> settings)
        {
            var services = new ServiceCollection();
            services.AddCatalogInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
            return services;
        }

        Assert.DoesNotContain(Build([]), d => d.ServiceType == typeof(IBackstageCatalogClient));

        var configured = Build(new() { ["Axiom:Catalog:Backstage:BaseUrl"] = "https://backstage.example.com", ["Axiom:Catalog:Backstage:Token"] = "t" });
        Assert.Contains(configured, d => d.ServiceType == typeof(IBackstageCatalogClient));
        using var provider = configured.BuildServiceProvider();
        Assert.IsType<BackstageCatalogClient>(provider.GetRequiredService<IBackstageCatalogClient>());

        using var invalid = Build(new() { ["Axiom:Catalog:Backstage:BaseUrl"] = "http://internal.example.com" }).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => invalid.GetRequiredService<IBackstageCatalogClient>());
    }
}
