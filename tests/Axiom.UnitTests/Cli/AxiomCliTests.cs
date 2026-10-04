using System.Net;
using System.Text;
using Axiom.Cli;

namespace Axiom.UnitTests.Cli;

public sealed class AxiomCliTests : IDisposable
{
    private const string Token = "super-secret-token";
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();
    private readonly StubHandler _handler = new();
    private readonly Dictionary<string, string?> _env = new() { ["AXIOM_URL"] = "https://axiom.example.com", ["AXIOM_TOKEN"] = Token };

    public void Dispose()
    {
        _stdout.Dispose();
        _stderr.Dispose();
        _handler.Dispose();
    }

    private Task<int> RunAsync(params string[] args) =>
        AxiomCli.RunAsync(args, _stdout, _stderr, name => _env.GetValueOrDefault(name), _handler, CancellationToken.None);

    private static string Evaluation(string verdict, string findings = "[]") =>
        $$"""{"evaluationId":"ev_1","receiptId":"rc_1","stage":"PR","snapshotId":"gs_1","verdict":"{{verdict}}","findings":{{findings}},"requiredActions":["Do the thing"]}""";

    private static readonly string[] PullRequest =
        ["evaluate-pr", "--repository", "gateway", "--base", "aaaaaaa", "--head", "bbbbbbb", "--pull-request", "42"];

    [Theory]
    [InlineData("ALLOW", 0)]
    [InlineData("ALLOW_WITH_WARNINGS", 0)]
    [InlineData("REQUIRE_REVIEW", 10)]
    [InlineData("BLOCK", 20)]
    public async Task The_exit_code_is_the_verdict(string verdict, int exit)
    {
        _handler.Respond(HttpStatusCode.OK, Evaluation(verdict));

        var code = await RunAsync(PullRequest);

        Assert.Equal(exit, code);
        Assert.Contains(verdict, _stdout.ToString(), StringComparison.Ordinal);
        Assert.Equal("", _stderr.ToString());
    }

    [Fact]
    public async Task The_request_goes_to_the_pr_endpoint_with_the_commit_coordinates_and_the_bearer_token()
    {
        _handler.Respond(HttpStatusCode.OK, Evaluation("ALLOW"));

        await RunAsync([.. PullRequest, "--design", "ev_design"]);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://axiom.example.com/v1/evaluations/pr", request.Uri);
        Assert.Equal($"Bearer {Token}", request.Authorization);
        Assert.Contains("\"headSha\":\"bbbbbbb\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"pullRequest\":\"42\"", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"designEvaluationId\":\"ev_design\"", request.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_failure_is_no_verdict_and_never_exit_zero(HttpStatusCode status)
    {
        _handler.Respond(status, """{"error":{"code":"SCM_UNAVAILABLE","retryable":true,"message":"The diff could not be read."}}""");

        var code = await RunAsync(PullRequest);

        Assert.Equal(3, code);
        Assert.Contains("SCM_UNAVAILABLE", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("No governance verdict was produced", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal("", _stdout.ToString());
    }

    [Fact]
    public async Task An_unreachable_server_and_a_non_json_answer_fail_closed_without_leaking_the_token()
    {
        _handler.Throw = new HttpRequestException($"connection to https://axiom.example.com failed for {Token}");
        var unreachable = await RunAsync(PullRequest);
        _handler.Throw = null;
        _handler.Respond(HttpStatusCode.OK, "<html>proxy error</html>");
        var html = await RunAsync(PullRequest);

        Assert.Equal((3, 3), (unreachable, html));
        Assert.DoesNotContain(Token, _stderr.ToString() + _stdout, StringComparison.Ordinal);
        Assert.Contains("HttpRequestException", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://axiom.example.com")]
    [InlineData("ftp://axiom.example.com")]
    [InlineData("not a url")]
    public async Task The_token_is_never_sent_over_an_insecure_or_invalid_url(string url)
    {
        _env["AXIOM_URL"] = url;

        var code = await RunAsync(PullRequest);

        Assert.Equal(3, code);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task Loopback_http_is_allowed_for_local_development_and_the_url_option_wins()
    {
        _env["AXIOM_URL"] = "https://elsewhere.example.com";
        _handler.Respond(HttpStatusCode.OK, Evaluation("ALLOW"));

        var code = await RunAsync([.. PullRequest, "--url", "http://localhost:5080"]);

        Assert.Equal(0, code);
        Assert.Equal("http://localhost:5080/v1/evaluations/pr", Assert.Single(_handler.Requests).Uri);
    }

    [Fact]
    public async Task A_missing_token_or_url_is_reported_without_a_request()
    {
        _env["AXIOM_TOKEN"] = null;
        var noToken = await RunAsync(PullRequest);
        _env["AXIOM_TOKEN"] = Token;
        _env["AXIOM_URL"] = null;
        var noUrl = await RunAsync(PullRequest);

        Assert.Equal((3, 3), (noToken, noUrl));
        Assert.Empty(_handler.Requests);
        Assert.Contains("AXIOM_TOKEN", _stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("AXIOM_URL", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_required_options_are_a_usage_error()
    {
        var code = await RunAsync("evaluate-pr", "--repository", "gateway");

        Assert.Equal(2, code);
        Assert.Empty(_handler.Requests);
    }

    private const string Findings = """
        [{"code":"POLICY_VIOLATION","severity":"BLOCK","message":"'src/A.cs' must not change, 100% sure.\nSecond line.","governanceIds":["ARCH-100"],"path":"src/A.cs","line":7},
         {"code":"SCOPE_EXPANSION","severity":"REQUIRE_REVIEW","message":"Outside design.","governanceIds":[]},
         {"code":"WAIVED_ONE","severity":"BLOCK","message":"Waived.","waivedBy":"EXC-1"}]
        """;

    [Fact]
    public async Task Text_output_lists_active_findings_with_location_and_next_actions()
    {
        _handler.Respond(HttpStatusCode.OK, Evaluation("BLOCK", Findings));

        await RunAsync(PullRequest);

        var output = _stdout.ToString();
        Assert.Contains("[BLOCK] POLICY_VIOLATION (ARCH-100) src/A.cs:7:", output, StringComparison.Ordinal);
        Assert.Contains("[REQUIRE_REVIEW] SCOPE_EXPANSION", output, StringComparison.Ordinal);
        Assert.DoesNotContain("WAIVED_ONE", output, StringComparison.Ordinal);
        Assert.Contains("next: Do the thing", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHub_format_emits_escaped_workflow_annotations()
    {
        _handler.Respond(HttpStatusCode.OK, Evaluation("BLOCK", Findings));

        await RunAsync([.. PullRequest, "--format", "github"]);

        Assert.Contains("::error file=src/A.cs,line=7,title=POLICY_VIOLATION::'src/A.cs' must not change, 100%25 sure.%0ASecond line.", _stdout.ToString(), StringComparison.Ordinal);
        Assert.Contains("::error title=SCOPE_EXPANSION::Outside design.", _stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Azure_format_emits_logging_commands()
    {
        _handler.Respond(HttpStatusCode.OK, Evaluation("BLOCK", Findings));

        await RunAsync([.. PullRequest, "--format", "azure"]);

        Assert.Contains("##vso[task.logissue type=error;code=POLICY_VIOLATION;sourcepath=src/A.cs;linenumber=7]'src/A.cs' must not change, 100%AZP25 sure.%0ASecond line.", _stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_format_prints_the_evaluation_unchanged()
    {
        _handler.Respond(HttpStatusCode.OK, Evaluation("ALLOW"));

        await RunAsync([.. PullRequest, "--format", "json"]);

        var printed = System.Text.Json.JsonDocument.Parse(_stdout.ToString()).RootElement;
        Assert.Equal("ev_1", printed.GetProperty("evaluationId").GetString());
        Assert.Equal("Do the thing", printed.GetProperty("requiredActions")[0].GetString());
    }

    [Fact]
    public async Task Diff_and_preflight_use_their_own_endpoints()
    {
        _handler.Respond(HttpStatusCode.OK, Evaluation("ALLOW"));

        await RunAsync("evaluate-diff", "--repository", "gateway", "--base", "aaaaaaa", "--head", "bbbbbbb");
        await RunAsync("preflight", "--repository", "gateway", "--ref", "main", "--task", "Add health endpoint", "--path", "src/Health.cs");

        Assert.Equal(["https://axiom.example.com/v1/evaluations/diff", "https://axiom.example.com/v1/evaluations/preflight"], _handler.Requests.Select(r => r.Uri));
        Assert.Contains("\"paths\":[\"src/Health.cs\"]", _handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Receipts_are_fetched_by_id_or_commit_and_an_unverified_receipt_fails()
    {
        _handler.Respond(HttpStatusCode.OK, """{"receiptId":"rc_1","verdict":"ALLOW","verified":true}""");
        var byId = await RunAsync("receipt", "--id", "rc_1");
        var byCommit = await RunAsync("receipt", "--repository", "gateway", "--commit", "bbbbbbb");
        _handler.Respond(HttpStatusCode.OK, """{"receiptId":"rc_1","verdict":"ALLOW","verified":false}""");
        var tampered = await RunAsync("receipt", "--id", "rc_1");
        var neither = await RunAsync("receipt");

        Assert.Equal((0, 0, 20, 2), (byId, byCommit, tampered, neither));
        Assert.Equal("https://axiom.example.com/v1/receipts?repository=gateway&commitSha=bbbbbbb", _handler.Requests[1].Uri);
        Assert.Contains("does not verify", _stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_governance_accepts_the_specification_examples_and_rejects_broken_records()
    {
        var root = Path.Combine(Path.GetTempPath(), "axiom-tests", "cli-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "decisions"));
            Directory.CreateDirectory(Path.Combine(root, "exceptions"));
            var examples = Path.Combine(AppContext.BaseDirectory, "SpecExamples");
            File.Copy(Path.Combine(examples, "decisions", "ARCH-042-gateway-routing.md"), Path.Combine(root, "decisions", "ARCH-042.md"));
            File.Copy(Path.Combine(examples, "exceptions", "EXC-023-legacy-homepage.yaml"), Path.Combine(root, "exceptions", "EXC-023.yaml"));
            File.WriteAllText(Path.Combine(root, "README.md"), "# not a record");

            var valid = await RunAsync("validate-governance", "--root", root);
            Assert.Equal(0, valid);
            Assert.Contains("2 record file(s) examined, 2 valid, 0 error(s)", _stdout.ToString(), StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(root, "decisions", "ARCH-099.yaml"), "schemaVersion: axiom.io/v1\nkind: Decision\nmetadata:\n  id: ARCH-099\n", Encoding.UTF8);
            var broken = await RunAsync("validate-governance", "--root", root);

            Assert.Equal(20, broken);
            Assert.Contains("decisions/ARCH-099.yaml", _stderr.ToString(), StringComparison.Ordinal);
            Assert.Equal(2, await RunAsync("validate-governance", "--root", Path.Combine(root, "missing")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record Sent(string Uri, string? Authorization, string Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "{}";

        public List<Sent> Requests { get; } = [];

        public Exception? Throw { get; set; }

        public void Respond(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Sent(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return Throw is not null
                ? throw Throw
                : new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }
}
