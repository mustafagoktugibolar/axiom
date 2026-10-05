using Axiom.Application.Catalog;
using Axiom.Domain.Catalog;
using Axiom.Infrastructure.Catalog.Manifests;

namespace Axiom.UnitTests.Catalog;

public class CatalogManifestParserTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private readonly CatalogManifestParser _parser = new();

    private CatalogManifestResult Parse(string content, string path = ".axiom/catalog.yaml") =>
        _parser.Parse("acme", "repo:gateway", path, content, ObservedAt);

    private static IEnumerable<string> Edges(CatalogImport import) =>
        import.Edges.Select(e => $"{e.From} {e.Relation} {e.To}");

    [Fact]
    public void Recognizes_axiom_and_backstage_manifest_paths()
    {
        Assert.Contains(".axiom/catalog.yaml", _parser.ManifestPaths);
        Assert.Contains("catalog-info.yaml", _parser.ManifestPaths);
    }

    [Fact]
    public void Parses_the_domain_model_example()
    {
        var result = Parse(
            """
            kind: Component
            metadata:
              id: component:gui/api-gateway
              name: api-gateway
            spec:
              system: system:gui-platform
              owner: team:platform
              repositories:
                - repo:gateway
              providesApis: []
              consumesApis:
                - api:homepage-service/v1
              dependsOn:
                - component:auth/auth-service
            """);

        Assert.Empty(result.Errors);
        var import = Assert.IsType<CatalogImport>(result.Import);
        Assert.Equal(ProvenanceSource.RepositoryManifest, import.Source);
        Assert.Equal("repo:gateway#.axiom/catalog.yaml", import.SourceLocator);
        Assert.Equal(["component:gui/api-gateway", "repo:gateway"], import.Entities.Select(e => e.Ref.ToString()));
        Assert.Equal("api-gateway", import.Entities[0].Title);
        Assert.Equal(
            [
                "system:gui-platform Contains component:gui/api-gateway",
                "component:gui/api-gateway Consumes api:homepage-service/v1",
                "component:gui/api-gateway DependsOn component:auth/auth-service",
                "repo:gateway Implements component:gui/api-gateway",
                "team:platform Owns component:gui/api-gateway",
            ],
            Edges(import));
        Assert.All(import.Entities, e => AssertDeclared(e.Provenance, import.SourceLocator));
        Assert.All(import.Edges, e => AssertDeclared(e.Provenance, import.SourceLocator));
    }

    private static void AssertDeclared(EntityProvenance provenance, string locator)
    {
        Assert.Equal(ProvenanceSource.RepositoryManifest, provenance.Source);
        Assert.Equal(locator, provenance.Locator);
        Assert.Equal(ObservedAt, provenance.FirstSeen);
        Assert.Equal(ObservedAt, provenance.LastSeen);
        Assert.True(provenance.IsFact);
    }

    [Fact]
    public void Parses_multiple_documents_with_capabilities_technologies_and_resources()
    {
        var result = Parse(
            """
            kind: Component
            metadata:
              id: component:orders
              title: Orders service
              description: Takes orders.
            spec:
              capabilities: [checkout]
              technologies: [dotnet, PostgreSQL, dotnet]
              resources: [database:orders-db, queue:orders-jobs, shared-cache]
              publishesTo: [stream:orders.created]
              subscribesTo: [payments.settled]
              deployments: [deployment:orders-prod]
            ---
            kind: Repository
            metadata:
              name: orders
            spec:
              cloneUrl: https://github.com/acme/orders.git
              cloneUrls: ["git@git.internal:acme/orders.git"]
              aliases: [acme/orders-service]
              components: [orders]
            ---
            kind: System
            metadata:
              id: system:commerce
            spec:
              domain: retail
              owner: commerce-team
            """);

        Assert.Empty(result.Errors);
        var import = result.Import!;
        var component = import.Entities.Single(e => e.Ref == EntityRef.Parse("component:orders"));
        Assert.Equal("Orders service", component.Title);
        Assert.Equal("Takes orders.", component.Description);
        Assert.Equal(["dotnet", "PostgreSQL"], component.Technologies.AsEnumerable());

        var repository = import.Entities.Single(e => e.Ref == EntityRef.Parse("repo:orders"));
        Assert.Equal("https://github.com/acme/orders.git git@git.internal:acme/orders.git", repository.Attributes[RepositoryAttributes.CloneUrls]);
        Assert.Equal("acme/orders-service", repository.Attributes[RepositoryAttributes.Aliases]);

        var edges = Edges(import).ToHashSet();
        Assert.Contains("component:orders Supports capability:checkout", edges);
        Assert.Contains("component:orders StoresIn database:orders-db", edges);
        Assert.Contains("component:orders DependsOn queue:orders-jobs", edges);
        Assert.Contains("component:orders DependsOn resource:shared-cache", edges);
        Assert.Contains("component:orders PublishesTo stream:orders.created", edges);
        Assert.Contains("component:orders SubscribesTo stream:payments.settled", edges);
        Assert.Contains("component:orders DeployedAs deployment:orders-prod", edges);
        Assert.Contains("repo:orders Implements component:orders", edges);
        Assert.Contains("domain:retail Contains system:commerce", edges);
        Assert.Contains("team:commerce-team Owns system:commerce", edges);
    }

    [Fact]
    public void Parses_backstage_catalog_info()
    {
        var result = Parse(
            """
            apiVersion: backstage.io/v1alpha1
            kind: Component
            metadata:
              name: api-gateway
              title: API Gateway
              tags: [go, edge]
              annotations:
                github.com/project-slug: Acme/Gateway
                axiom.dev/technologies: go, envoy
            spec:
              type: service
              lifecycle: production
              owner: platform
              system: gui-platform
              providesApis: [gateway-api]
              consumesApis: [api:homepage/homepage-v1]
              dependsOn: [component:auth-service, resource:default/sessions-db]
            ---
            apiVersion: backstage.io/v1alpha1
            kind: API
            metadata: { name: gateway-api }
            spec: { type: openapi, owner: group:platform, system: gui-platform }
            ---
            apiVersion: backstage.io/v1alpha1
            kind: Resource
            metadata: { name: sessions-db }
            spec: { type: database, owner: user:jane }
            ---
            apiVersion: backstage.io/v1alpha1
            kind: System
            metadata: { name: gui-platform }
            spec: { owner: platform, domain: gui }
            ---
            apiVersion: backstage.io/v1alpha1
            kind: Domain
            metadata: { name: gui }
            spec: { owner: platform }
            ---
            apiVersion: backstage.io/v1alpha1
            kind: Group
            metadata: { name: platform }
            spec: { type: team, profile: { displayName: Platform Team }, children: [] }
            ---
            apiVersion: backstage.io/v1alpha1
            kind: User
            metadata: { name: jane }
            spec: { memberOf: [platform] }
            """,
            "catalog-info.yaml");

        Assert.Empty(result.Errors);
        var import = result.Import!;
        Assert.Equal(ProvenanceSource.RepositoryManifest, import.Source);
        Assert.Equal(
            ["domain:gui", "system:gui-platform", "component:api-gateway", "repo:acme/gateway", "api:gateway-api", "resource:sessions-db", "team:platform"],
            import.Entities.Select(e => e.Ref.ToString()));

        var component = import.Entities.Single(e => e.Ref.Kind == EntityKind.Component);
        Assert.Equal("API Gateway", component.Title);
        Assert.Equal("service", component.Attributes["type"]);
        Assert.Equal("go edge", component.Attributes["tags"]);
        Assert.Equal(["envoy", "go"], component.Technologies.AsEnumerable());
        Assert.Equal("Platform Team", import.Entities.Single(e => e.Ref.Kind == EntityKind.Team).Title);
        Assert.Equal("https://github.com/Acme/Gateway", import.Entities.Single(e => e.Ref.Kind == EntityKind.Repository).Attributes[RepositoryAttributes.CloneUrls]);

        var edges = Edges(import).ToHashSet();
        Assert.Contains("team:platform Owns component:api-gateway", edges);
        Assert.Contains("system:gui-platform Contains component:api-gateway", edges);
        Assert.Contains("component:api-gateway Provides api:gateway-api", edges);
        Assert.Contains("component:api-gateway Consumes api:homepage/homepage-v1", edges);
        Assert.Contains("component:api-gateway DependsOn component:auth-service", edges);
        Assert.Contains("component:api-gateway DependsOn resource:sessions-db", edges);
        Assert.Contains("repo:acme/gateway Implements component:api-gateway", edges);
        Assert.Contains("domain:gui Contains system:gui-platform", edges);
        Assert.Contains("team:platform Owns domain:gui", edges);
        // A user owner has no counterpart in the graph: no edge is invented for it.
        Assert.DoesNotContain(edges, e => e.EndsWith("Owns resource:sessions-db", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_manifest_is_a_valid_empty_import()
    {
        var result = Parse("# nothing declared yet\n");

        Assert.Empty(result.Errors);
        Assert.Empty(result.Import!.Entities);
        Assert.Empty(result.Import.Edges);
    }

    [Theory]
    [InlineData("kind: Component\nmetadata: [unclosed", "not valid YAML")]
    [InlineData("- just\n- a list", "must be a mapping")]
    [InlineData("metadata:\n  id: component:x", "kind is required")]
    [InlineData("kind: Widget\nmetadata:\n  id: component:x", "unknown kind 'Widget'")]
    [InlineData("kind: Component\nmetadata:\n  id: not-a-ref", "not a valid entity reference")]
    [InlineData("kind: Component\nmetadata:\n  id: api:x", "does not match kind")]
    [InlineData("kind: Component\nmetadata:\n  title: nameless", "metadata.id (kind:name) or a valid metadata.name is required")]
    [InlineData("kind: Component\nmetadata:\n  id: component:x\nspec:\n  owner: [a, b]", "spec.owner must be a string")]
    [InlineData("kind: Component\nmetadata:\n  id: component:x\nspec:\n  dependsOn: auth", "spec.dependsOn must be a list")]
    [InlineData("kind: Component\nmetadata:\n  id: component:x\nspec:\n  dependsOn: ['bogus:thing']", "'bogus:thing' is not a valid entity reference")]
    [InlineData("kind: Component\nmetadata:\n  id: component:x\nspec:\n  ownr: team:a", "spec.ownr is not a known field")]
    [InlineData("kind: Component\nmetadata:\n  id: component:x\nspec:\n  cloneUrl: https://github.com/a/b", "apply only to kind Repository")]
    [InlineData("kind: Component\nmetadata:\n  id: component:x\n---\nkind: Component\nmetadata:\n  id: component:x", "declared more than once")]
    [InlineData("kind: Component\nmetadata: {id: component:x}\nspec: 7", "spec must be a mapping")]
    [InlineData("apiVersion: backstage.io/v1alpha1\nkind: Component\nmetadata: {}", "metadata.name is required")]
    [InlineData("apiVersion: backstage.io/v1alpha1\nkind: Component\nmetadata: {name: x}\nspec: {dependsOn: ['component:a/b/c']}", "not a valid entity reference")]
    public void Malformed_input_yields_errors_and_no_import(string content, string expectedError)
    {
        var result = Parse(content);

        Assert.Null(result.Import);
        Assert.Contains(result.Errors, e => e.Contains(expectedError, StringComparison.Ordinal));
        Assert.All(result.Errors, e => Assert.StartsWith(".axiom/catalog.yaml:", e, StringComparison.Ordinal));
    }

    [Fact]
    public void One_bad_document_rejects_the_whole_file_and_names_the_document()
    {
        var result = Parse("kind: Component\nmetadata:\n  id: component:good\n---\nkind: Component\nmetadata:\n  id: nope\n");

        Assert.Null(result.Import);
        Assert.StartsWith(".axiom/catalog.yaml: document 2:", Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Anchor_expansion_bombs_are_rejected_not_expanded()
    {
        var yaml = "a: &a [x, x, x, x, x, x, x, x, x, x]\n"
            + string.Concat(Enumerable.Range(0, 8).Select(i => $"{(char)('b' + i)}: &{(char)('b' + i)} [*{(char)('a' + i)}, *{(char)('a' + i)}, *{(char)('a' + i)}, *{(char)('a' + i)}, *{(char)('a' + i)}, *{(char)('a' + i)}, *{(char)('a' + i)}, *{(char)('a' + i)}, *{(char)('a' + i)}, *{(char)('a' + i)}]\n"));

        var result = Parse(yaml);

        Assert.Null(result.Import);
        Assert.Contains("not valid YAML", Assert.Single(result.Errors), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/opt/axiom-demo-gateway", true)]
    [InlineData("file:///srv/git/gateway", true)]
    [InlineData("C:/repos/gateway", true)]
    [InlineData("not a url", false)]
    public void A_local_clone_location_is_accepted_but_a_malformed_one_is_not(string cloneUrl, bool accepted)
    {
        var result = Parse(string.Join('\n', "kind: Repository", "metadata:", "  id: repo:gateway", "spec:", $"  cloneUrls: ['{cloneUrl}']"));

        Assert.Equal(accepted, result.Import is not null);
    }
}
