using Axiom.Domain.Catalog;

namespace Axiom.UnitTests.Catalog;

public class EntityRefTests
{
    [Theory]
    [InlineData("component:gui/api-gateway", EntityKind.Component, "gui/api-gateway", "api-gateway")]
    [InlineData("repo:gateway", EntityKind.Repository, "gateway", "gateway")]
    [InlineData("API:Homepage-Service/v1", EntityKind.Api, "homepage-service/v1", "v1")]
    [InlineData(" team: platform ", EntityKind.Team, "platform", "platform")]
    [InlineData("stream:orders.created", EntityKind.EventStream, "orders.created", "orders.created")]
    public void Parse_reads_kind_and_normalized_name(string text, EntityKind kind, string name, string simpleName)
    {
        var reference = EntityRef.Parse(text);

        Assert.Equal(kind, reference.Kind);
        Assert.Equal(name, reference.Name);
        Assert.Equal(simpleName, reference.SimpleName);
        Assert.Equal(reference, EntityRef.Parse(reference.ToString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("gateway")]
    [InlineData("unknown:gateway")]
    [InlineData("repo:")]
    [InlineData(":gateway")]
    [InlineData("repo:a b")]
    [InlineData("repo:a:b")]
    [InlineData("repo:/gateway")]
    [InlineData("repo:gateway/")]
    public void TryParse_rejects_malformed_references(string? text)
    {
        Assert.False(EntityRef.TryParse(text, out _));
        if (text is not null)
        {
            Assert.Throws<FormatException>(() => EntityRef.Parse(text));
        }
    }

    [Theory]
    [InlineData("component:gui/api-gateway", true)]
    [InlineData("gui/api-gateway", true)]
    [InlineData("api-gateway", true)]
    [InlineData("API-Gateway", true)]
    [InlineData(" api-gateway ", true)]
    [InlineData("gui", false)]
    [InlineData("repo:gui/api-gateway", false)]
    [InlineData("gateway", false)]
    public void IsDesignatedBy_accepts_reference_namespaced_and_simple_name(string scopeValue, bool expected) =>
        Assert.Equal(expected, EntityRef.Parse("component:gui/api-gateway").IsDesignatedBy(scopeValue));

    [Fact]
    public void References_order_by_kind_then_name()
    {
        var ordered = new[] { "repo:b", "component:z", "repo:a", "domain:x" }.Select(EntityRef.Parse).Order().Select(r => r.ToString());

        Assert.Equal(["domain:x", "component:z", "repo:a", "repo:b"], ordered);
    }

    [Theory]
    [InlineData("https://github.com/Acme/Gateway.git", "github.com/Acme/Gateway")]
    [InlineData("https://GitHub.com/Acme/Gateway/", "github.com/Acme/Gateway")]
    [InlineData("https://user:secret@github.com/Acme/Gateway.git", "github.com/Acme/Gateway")]
    [InlineData("git@github.com:Acme/Gateway.git", "github.com/Acme/Gateway")]
    [InlineData("ssh://git@github.com:22/Acme/Gateway.git", "github.com/Acme/Gateway")]
    [InlineData("ssh://git@git.example.com:7999/proj/repo.git", "git.example.com:7999/proj/repo")]
    [InlineData("https://github.com:443/Acme/Gateway", "github.com/Acme/Gateway")]
    [InlineData("github.com/Acme/Gateway", "github.com/Acme/Gateway")]
    [InlineData("https://github.com/Acme/Gateway.git?ref=main#readme", "github.com/Acme/Gateway")]
    [InlineData("  https://github.com/Acme/Gateway.GIT//  ", "github.com/Acme/Gateway")]
    public void Clone_urls_normalize_to_host_and_path(string url, string expected) =>
        Assert.Equal(expected, RepositoryUrl.Normalize(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https:///path")]
    [InlineData("not a url")]
    public void Values_without_a_host_do_not_normalize(string? value) => Assert.Null(RepositoryUrl.Normalize(value));

    [Fact]
    public void Url_path_is_lower_cased_and_absent_for_bare_hosts()
    {
        Assert.Equal("acme/gateway", RepositoryUrl.PathOf("github.com/Acme/Gateway"));
        Assert.Null(RepositoryUrl.PathOf("github.com"));
    }

    [Fact]
    public void Provenance_treats_declared_sources_as_fact_and_inference_as_candidate()
    {
        var seen = DateTimeOffset.UnixEpoch;

        Assert.True(EntityProvenance.Declared(ProvenanceSource.Catalog, "x", seen).IsFact);
        Assert.True(EntityProvenance.Declared(ProvenanceSource.RepositoryManifest, "x", seen).IsFact);
        Assert.False(EntityProvenance.Declared(ProvenanceSource.CodeInference, "x", seen).IsFact);
        Assert.False(EntityProvenance.Declared(ProvenanceSource.LlmInference, "x", seen).IsFact);
        Assert.True((EntityProvenance.Declared(ProvenanceSource.LlmInference, "x", seen) with { Confirmed = true }).IsFact);
    }
}
