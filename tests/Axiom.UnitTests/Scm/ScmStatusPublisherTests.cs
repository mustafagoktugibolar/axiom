using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Axiom.Application.Catalog;
using Axiom.Application.Evaluation;
using Axiom.Domain.Audit;
using Axiom.Domain.Catalog;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;
using Axiom.Infrastructure.Scm;
using Axiom.UnitTests.Evaluation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Axiom.UnitTests.Scm;

public sealed class ScmStatusPublisherTests : IDisposable
{
    private const string Org = "acme";
    private readonly CapturingHandler _handler = new();
    private readonly ScmOptions _settings = new() { PublishStatus = true };

    public void Dispose() => _handler.Dispose();

    private ScmStatusPublisher Publisher(string cloneUrl) =>
        new(new UrlGraph(cloneUrl), new StaticOptions(_settings), new FakeHttpClients(_handler));

    private static ScmStatusReport Report(Verdict verdict = Verdict.Block, string? pullRequest = "42") =>
        new(Org, "gateway", "abc1234", pullRequest, verdict, "Axiom: blocked", "Verdict: BLOCK", "https://axiom.example/evaluations/ev_1", "ev_1");

    private void GitHub(string host = "github.com", string? api = null) =>
        _settings.Providers[host] = new ScmProviderOptions { Type = ScmProviderType.GitHub, Token = "gh-secret", ApiBaseUrl = api };

    private void AzureDevOps(string host = "dev.azure.com") =>
        _settings.Providers[host] = new ScmProviderOptions { Type = ScmProviderType.AzureDevOps, Token = "pat-secret" };

    [Theory]
    [InlineData(Verdict.Allow, "success")]
    [InlineData(Verdict.AllowWithWarnings, "success")]
    [InlineData(Verdict.RequireReview, "action_required")]
    [InlineData(Verdict.Block, "failure")]
    public async Task GitHub_check_run_maps_the_verdict_and_names_the_commit(Verdict verdict, string conclusion)
    {
        GitHub();

        var published = await Publisher("https://github.com/acme/gateway.git").PublishAsync(Report(verdict), default);

        Assert.True(published);
        var sent = Assert.Single(_handler.Requests);
        Assert.Equal("https://api.github.com/repos/acme/gateway/check-runs", sent.Uri);
        Assert.Equal("Bearer gh-secret", sent.Authorization);
        var body = JsonDocument.Parse(sent.Body).RootElement;
        Assert.Equal(("abc1234", "completed", conclusion, "ev_1"),
            (body.GetProperty("head_sha").GetString(), body.GetProperty("status").GetString(), body.GetProperty("conclusion").GetString(), body.GetProperty("external_id").GetString()));
        Assert.Equal("Axiom governance", body.GetProperty("name").GetString());
        Assert.Equal("Verdict: BLOCK", body.GetProperty("output").GetProperty("summary").GetString());
    }

    [Fact]
    public async Task GitHub_enterprise_uses_its_own_api_base()
    {
        GitHub("ghe.example.com", "https://ghe.example.com/api/v3");

        await Publisher("https://ghe.example.com/platform/gateway").PublishAsync(Report(), default);

        Assert.Equal("https://ghe.example.com/api/v3/repos/platform/gateway/check-runs", Assert.Single(_handler.Requests).Uri);
    }

    [Theory]
    [InlineData("http://api.github.com")]
    [InlineData("https://attacker.example")]
    [InlineData("https://user:pw@api.github.com")]
    public async Task The_github_token_is_never_sent_to_an_untrusted_api_base(string api)
    {
        GitHub(api: api);

        var published = await Publisher("https://github.com/acme/gateway").PublishAsync(Report(), default);

        Assert.False(published);
        Assert.Empty(_handler.Requests);
    }

    [Theory]
    [InlineData(Verdict.Allow, "succeeded")]
    [InlineData(Verdict.RequireReview, "pending")]
    [InlineData(Verdict.Block, "failed")]
    public async Task Azure_devops_posts_a_pull_request_status(Verdict verdict, string state)
    {
        AzureDevOps();

        var published = await Publisher("https://dev.azure.com/acme/Platform/_git/gateway").PublishAsync(Report(verdict), default);

        Assert.True(published);
        var sent = Assert.Single(_handler.Requests);
        Assert.Equal("https://dev.azure.com/acme/Platform/_apis/git/repositories/gateway/pullRequests/42/statuses?api-version=7.1", sent.Uri);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":pat-secret")), sent.Authorization);
        var body = JsonDocument.Parse(sent.Body).RootElement;
        Assert.Equal(state, body.GetProperty("state").GetString());
        Assert.Equal("governance", body.GetProperty("context").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Azure_devops_falls_back_to_a_commit_status_without_a_pull_request()
    {
        AzureDevOps();

        await Publisher("https://dev.azure.com/acme/Platform/_git/gateway").PublishAsync(Report(pullRequest: null), default);

        Assert.Equal("https://dev.azure.com/acme/Platform/_apis/git/repositories/gateway/commits/abc1234/statuses?api-version=7.1", Assert.Single(_handler.Requests).Uri);
    }

    [Fact]
    public async Task Nothing_is_sent_when_publishing_is_off_or_no_provider_matches()
    {
        GitHub();
        _settings.PublishStatus = false;
        Assert.False(await Publisher("https://github.com/acme/gateway").PublishAsync(Report(), default));

        _settings.PublishStatus = true;
        Assert.False(await Publisher("https://gitlab.example.com/acme/gateway").PublishAsync(Report(), default));
        Assert.False(await Publisher("ssh://git@github.com/acme/gateway").PublishAsync(Report(), default));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task A_rejected_status_is_an_error_that_the_reporter_absorbs()
    {
        GitHub();
        _handler.Status = HttpStatusCode.Forbidden;
        var publisher = Publisher("https://github.com/acme/gateway");

        await Assert.ThrowsAsync<HttpRequestException>(() => publisher.PublishAsync(Report(), default));

        var reporter = new PullRequestStatusReporter(publisher, NullLogger<PullRequestStatusReporter>.Instance);
        Assert.False(await reporter.ReportAsync(Stored(Verdict.Block), null, default));
    }

    [Fact]
    public async Task The_reporter_only_reports_pull_request_evaluations_with_a_commit()
    {
        GitHub();
        var reporter = new PullRequestStatusReporter(Publisher("https://github.com/acme/gateway"), NullLogger<PullRequestStatusReporter>.Instance);

        Assert.False(await reporter.ReportAsync(Stored(Verdict.Allow, EvaluationStage.Diff), null, default));
        Assert.True(await reporter.ReportAsync(Stored(Verdict.Allow), "https://axiom.example/", default));
        Assert.Equal("https://axiom.example/evaluations/ev_1", JsonDocument.Parse(Assert.Single(_handler.Requests).Body).RootElement.GetProperty("details_url").GetString());
    }

    [Fact]
    public void The_summary_lists_active_findings_only_and_is_bounded()
    {
        var findings = Enumerable.Range(0, 14).Select(i => new Finding
        {
            Code = $"CODE_{i:00}", Severity = EnforcementLevel.Block, Source = FindingSource.Policy, Message = "m", GovernanceIds = ["ARCH-1"], Path = $"src/{i}.cs",
        }).Append(new Finding { Code = "WAIVED", Severity = EnforcementLevel.Block, Source = FindingSource.Policy, Message = "m", WaivedBy = "EXC-1" }).ToImmutableArray();

        var report = ScmStatusReports.For(Stored(Verdict.Block, findings: findings), null);

        Assert.Equal("Axiom: blocked by governance", report.Title);
        Assert.Contains("[BLOCK] CODE_00 (ARCH-1) src/0.cs", report.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("WAIVED", report.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("CODE_10", report.Summary, StringComparison.Ordinal);
        Assert.Contains("... and 4 more finding(s).", report.Summary, StringComparison.Ordinal);
        Assert.Null(report.DetailsUrl);
    }

    private static StoredEvaluation Stored(Verdict verdict, EvaluationStage stage = EvaluationStage.PullRequest, ImmutableArray<Finding>? findings = null)
    {
        var evaluation = new EvaluationRecord
        {
            Id = "ev_1",
            OrganizationId = Org,
            Stage = stage,
            CreatedAt = DateTimeOffset.UnixEpoch,
            Actor = new ActorIdentity("ci", null, null, null),
            Scm = new ScmCoordinates("gateway", null, "abc1234", "base", "42"),
            RequestFingerprint = "fp",
            SnapshotId = "gs_1",
            SourceCommit = "c1",
            SnapshotPublishedAt = DateTimeOffset.UnixEpoch,
            CatalogVersion = "cv_1",
            ResolvedScope = new ResolvedScopeView(ImmutableSortedDictionary<string, ImmutableArray<string>>.Empty, []),
            Significance = new SignificanceAssessment(false, [], []),
            Findings = findings ?? [],
            Verdict = verdict,
        };
        return new StoredEvaluation(evaluation, ReceiptFactory.Create(evaluation, ReceiptFactory.GenesisDigest));
    }

    private sealed record Sent(string Uri, string? Authorization, string Body);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<Sent> Requests { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Sent(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(Status);
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
