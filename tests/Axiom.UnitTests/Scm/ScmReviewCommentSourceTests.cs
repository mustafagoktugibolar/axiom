using System.Collections.Immutable;
using System.Net;
using System.Text;
using Axiom.Application.Candidates;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Domain.Catalog;
using Axiom.Domain.Governance;
using Axiom.Infrastructure.Scm;
using Microsoft.Extensions.Options;

namespace Axiom.UnitTests.Scm;

public sealed class ScmReviewCommentSourceTests : IDisposable
{
    private const string Org = "acme";
    private readonly RoutingHandler _handler = new();
    private readonly ScmOptions _settings = new();

    public void Dispose() => _handler.Dispose();

    private ScmReviewCommentSource Source(string cloneUrl) =>
        new(new UrlGraph(cloneUrl), new StaticOptions(_settings), new FakeHttpClients(_handler));

    private static ReviewCommentRequest Request(int max = 50) => new(Org, "gateway", max);

    [Fact]
    public async Task GitHub_comments_are_read_with_the_provider_token_from_the_api_host_only()
    {
        _settings.Providers["github.com"] = new ScmProviderOptions { Type = ScmProviderType.GitHub, Token = "gh-secret" };
        _handler.Respond("https://api.github.com/repos/acme/gateway/pulls/comments?sort=created&direction=desc&per_page=100&page=1", """
            [
              {"id": 101, "pull_request_url": "https://api.github.com/repos/acme/gateway/pulls/7", "user": {"login": "alice"}, "body": "Add a timeout.", "path": "src/A.cs", "html_url": "https://github.com/acme/gateway/pull/7#r101", "created_at": "2026-09-01T10:00:00Z"},
              {"id": 102, "pull_request_url": "https://api.github.com/repos/acme/gateway/pulls/8", "user": {"login": "bob"}, "body": "", "created_at": "2026-09-02T10:00:00Z"}
            ]
            """);

        var batch = await Source("https://github.com/acme/gateway.git").GetRecentAsync(Request(), default);

        var comment = Assert.Single(batch.Comments);
        Assert.Equal(("7", "101", "alice", "Add a timeout.", "src/A.cs"), (comment.PullRequestId, comment.CommentId, comment.Author, comment.Body, comment.FilePath));
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero), comment.CreatedAt);
        Assert.False(batch.Truncated);
        var sent = Assert.Single(_handler.Sent);
        Assert.Equal("Bearer gh-secret", sent.Authorization);
        Assert.Equal("api.github.com", sent.Host);
    }

    [Fact]
    public async Task Azure_DevOps_reads_threads_of_recent_pull_requests_and_skips_system_comments()
    {
        _settings.Providers["dev.azure.com"] = new ScmProviderOptions { Type = ScmProviderType.AzureDevOps, Token = "pat-secret" };
        _handler.Respond("https://dev.azure.com/acme/shop/_apis/git/repositories/gateway/pullrequests?searchCriteria.status=all&$top=50&api-version=7.1",
            """{"value": [{"pullRequestId": 12}, {"pullRequestId": 13}]}""");
        _handler.Respond("https://dev.azure.com/acme/shop/_apis/git/repositories/gateway/pullrequests/12/threads?api-version=7.1", """
            {"value": [
              {"id": 1, "threadContext": {"filePath": "/src/A.cs"}, "comments": [
                {"id": 1, "commentType": "text", "content": "Add a timeout.", "author": {"displayName": "Alice"}, "publishedDate": "2026-09-01T10:00:00Z"},
                {"id": 2, "commentType": "system", "content": "Alice updated the PR", "author": {"displayName": "Alice"}}
              ]}
            ]}
            """);
        _handler.Respond("https://dev.azure.com/acme/shop/_apis/git/repositories/gateway/pullrequests/13/threads?api-version=7.1", """{"value": []}""");

        var batch = await Source("https://dev.azure.com/acme/shop/_git/gateway").GetRecentAsync(Request(), default);

        var comment = Assert.Single(batch.Comments);
        Assert.Equal(("12", "1-1", "Alice", "Add a timeout.", "/src/A.cs"), (comment.PullRequestId, comment.CommentId, comment.Author, comment.Body, comment.FilePath));
        Assert.Equal(2, batch.PullRequestsScanned);
        Assert.All(_handler.Sent, s => Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":pat-secret")), s.Authorization));
    }

    [Fact]
    public async Task A_host_without_a_token_is_never_contacted()
    {
        _settings.Providers["github.com"] = new ScmProviderOptions { Type = ScmProviderType.GitHub, Token = null };

        await Assert.ThrowsAsync<AxiomException>(() => Source("https://github.com/acme/gateway.git").GetRecentAsync(Request(), default));
        Assert.Empty(_handler.Sent);
    }

    [Theory]
    [InlineData("http://github.com/acme/gateway.git")]
    [InlineData("https://unlisted.example.com/acme/gateway.git")]
    public async Task Only_https_hosts_with_a_configured_provider_are_used(string cloneUrl)
    {
        _settings.Providers["github.com"] = new ScmProviderOptions { Type = ScmProviderType.GitHub, Token = "gh-secret" };

        await Assert.ThrowsAsync<AxiomException>(() => Source(cloneUrl).GetRecentAsync(Request(), default));
        Assert.Empty(_handler.Sent);
    }

    [Fact]
    public async Task A_custom_github_api_base_must_be_https_and_github_com_must_use_the_public_api()
    {
        _settings.Providers["github.com"] = new ScmProviderOptions { Type = ScmProviderType.GitHub, Token = "gh-secret", ApiBaseUrl = "https://evil.example.com/" };

        await Assert.ThrowsAsync<AxiomException>(() => Source("https://github.com/acme/gateway.git").GetRecentAsync(Request(), default));
        Assert.Empty(_handler.Sent);
    }

    [Fact]
    public async Task A_redirect_is_an_error_and_is_not_followed()
    {
        _settings.Providers["github.com"] = new ScmProviderOptions { Type = ScmProviderType.GitHub, Token = "gh-secret" };
        _handler.Status = HttpStatusCode.Found;

        await Assert.ThrowsAsync<HttpRequestException>(() => Source("https://github.com/acme/gateway.git").GetRecentAsync(Request(), default));
        Assert.Single(_handler.Sent);
    }

    [Fact]
    public async Task GitHub_comments_beyond_the_pull_request_window_are_dropped_and_flagged()
    {
        _settings.Providers["github.com"] = new ScmProviderOptions { Type = ScmProviderType.GitHub, Token = "gh-secret" };
        var items = string.Join(',', Enumerable.Range(1, 100).Select(i =>
            $$"""{"id": {{i}}, "pull_request_url": "https://api.github.com/repos/acme/gateway/pulls/{{i}}", "user": {"login": "a"}, "body": "x y z", "created_at": "2026-09-01T10:00:00Z"}"""));
        _handler.Respond("https://api.github.com/repos/acme/gateway/pulls/comments?sort=created&direction=desc&per_page=100&page=1", $"[{items}]");

        var batch = await Source("https://github.com/acme/gateway.git").GetRecentAsync(Request(max: 10), default);

        Assert.Equal(10, batch.PullRequestsScanned);
        Assert.True(batch.Truncated);
    }

    private sealed record Sent(string Uri, string Host, string? Authorization);

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _routes = new(StringComparer.Ordinal);

        public List<Sent> Sent { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public void Respond(string uri, string json) => _routes[uri] = json;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            Sent.Add(new Sent(uri, request.RequestUri.Host, request.Headers.Authorization?.ToString()));
            if (Status != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(Status));
            }

            return Task.FromResult(_routes.TryGetValue(uri, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class FakeHttpClients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StaticOptions(ScmOptions value) : IOptionsMonitor<ScmOptions>
    {
        public ScmOptions CurrentValue => value;

        public ScmOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ScmOptions, string?> listener) => null;
    }

    private sealed class UrlGraph(string cloneUrl) : ISystemGraph
    {
        private static readonly EntityRef Repository = EntityRef.Parse("repo:gateway");

        public Task<EntityRef?> ResolveRepositoryAsync(string organizationId, string repository, CancellationToken cancellationToken) => Task.FromResult<EntityRef?>(Repository);

        public Task<SoftwareEntity?> FindAsync(string organizationId, EntityRef entity, CancellationToken cancellationToken) =>
            Task.FromResult<SoftwareEntity?>(new SoftwareEntity(Repository, "gateway", null, [],
                ImmutableSortedDictionary<string, string>.Empty.Add(RepositoryAttributes.CloneUrls, cloneUrl),
                EntityProvenance.Declared(ProvenanceSource.Manual, "test", DateTimeOffset.UnixEpoch)));

        public Task<RepositoryTopology> GetRepositoryTopologyAsync(string organizationId, EntityRef repository, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ImpactResult> TraverseAsync(string organizationId, ImpactQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<GraphQualityReport> GetQualityReportAsync(string organizationId, TimeSpan staleAfter, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<GraphView> GetViewAsync(string organizationId, EntityRef? focus, int depth, int maxNodes, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> GetCatalogVersionAsync(string organizationId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
