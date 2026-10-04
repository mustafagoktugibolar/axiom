using System.Text.Json;
using Axiom.IntegrationTests.Support;
using static Axiom.IntegrationTests.Evaluation.ApiHost;

namespace Axiom.IntegrationTests.Evaluation;

/// <summary>Diff and pull-request validation through the real HTTP host, Git repositories and PostgreSQL (R7, R8).</summary>
[Collection(PostgresTests.Name)]
public sealed class DiffEvaluationApiTests(PostgresFixture postgres)
{
    private static string[] FindingCodes(JsonElement result) => [.. result.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("code").GetString()!)];

    private async Task<(ApiHost Host, string Base, string Head)> ArrangeAsync(Action<Axiom.IntegrationTests.Governance.GovernanceRepo> change, bool allowLocalScm = true)
    {
        var host = new ApiHost(postgres, allowLocalScm);
        host.Governance.Write("governance/decisions/ARCH-100.yaml", GovernanceFixtures.GeneratedCodeDecision()).Commit("governance");
        host.Code.Write("src/Program.cs", "class Program;\n").Write("README.md", "# gateway\n");
        var baseSha = host.Code.Commit("base");
        change(host.Code);
        var head = host.Code.Commit("change");
        await host.SeedAsync();
        return (host, baseSha, head);
    }

    [Fact]
    public async Task A_forbidden_change_blocks_with_the_rule_evidence_and_a_receipt()
    {
        var (host, baseSha, head) = await ArrangeAsync(code => code.Write("src/Generated/Client.cs", "class Client;\n"));
        await using var scope = host;
        using var client = host.Client();

        var (status, body) = await PostAsync(client, "/v1/evaluations/diff", new { repository = "gateway", baseSha, headSha = head });

        Assert.Equal(200, status);
        Assert.Equal("DIFF", body.GetProperty("stage").GetString());
        Assert.Equal("BLOCK", body.GetProperty("verdict").GetString());
        var finding = Assert.Single(body.GetProperty("findings").EnumerateArray(), f => f.GetProperty("code").GetString() == "POLICY_VIOLATION");
        Assert.Equal("src/Generated/Client.cs", finding.GetProperty("path").GetString());
        Assert.Equal("forbidden-path-change", finding.GetProperty("ruleId").GetString());
        Assert.Equal(["ARCH-100"], finding.GetProperty("governanceIds").EnumerateArray().Select(i => i.GetString()));
        var run = Assert.Single(body.GetProperty("policyRuns").EnumerateArray());
        Assert.Equal("violated", run.GetProperty("outcome").GetString());
        Assert.StartsWith("sha256:", run.GetProperty("ruleHash").GetString(), StringComparison.Ordinal);
        Assert.Equal(["src/Generated/Client.cs"], body.GetProperty("resolvedScope").GetProperty("paths").EnumerateArray().Select(p => p.GetString()));

        using var auditor = host.Client("auditor");
        using var response = await auditor.GetAsync(new Uri($"/v1/receipts/{body.GetProperty("receiptId").GetString()}", UriKind.Relative));
        var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body.GetProperty("evaluationId").GetString(), receipt.GetProperty("evaluationId").GetString());
        Assert.Equal("BLOCK", receipt.GetProperty("verdict").GetString());
        Assert.Equal(head, receipt.GetProperty("commitSha").GetString());
        Assert.True(receipt.GetProperty("verified").GetBoolean());
    }

    [Fact]
    public async Task A_clean_change_is_allowed_and_the_same_request_returns_the_same_evaluation()
    {
        var (host, baseSha, head) = await ArrangeAsync(code => code.Write("src/Program.cs", "class Program { }\n"));
        await using var scope = host;
        using var client = host.Client();
        var request = new { repository = "gateway", baseSha, headSha = head };

        var (_, first) = await PostAsync(client, "/v1/evaluations/diff", request);
        var (_, second) = await PostAsync(client, "/v1/evaluations/diff", request);

        Assert.Equal("ALLOW", first.GetProperty("verdict").GetString());
        Assert.Equal(first.GetProperty("evaluationId").GetString(), second.GetProperty("evaluationId").GetString());
        Assert.Equal(first.GetProperty("receiptId").GetString(), second.GetProperty("receiptId").GetString());
        Assert.Equal("PASSED", Assert.Single(first.GetProperty("policyRuns").EnumerateArray()).GetProperty("outcome").GetString()!.ToUpperInvariant());
    }

    [Fact]
    public async Task A_rename_out_of_a_protected_path_is_caught_at_the_old_path()
    {
        var (host, _, generatedHead) = await ArrangeAsync(code =>
            code.Write("src/Generated/Old.cs", "class Old { int a; int b; int c; }\n"));
        await using var scope = host;
        using var client = host.Client();
        host.Code.Move("src/Generated/Old.cs", "src/Moved.cs");
        var moved = host.Code.Commit("move");

        var (status, body) = await PostAsync(client, "/v1/evaluations/diff", new { repository = "gateway", baseSha = generatedHead, headSha = moved });

        Assert.Equal(200, status);
        Assert.Equal("BLOCK", body.GetProperty("verdict").GetString());
        Assert.Contains("src/Generated/Old.cs", body.GetProperty("findings").EnumerateArray().Select(f => f.TryGetProperty("path", out var p) ? p.GetString() : null));
    }

    [Fact]
    public async Task A_significant_diff_without_a_design_requires_review()
    {
        var (host, baseSha, head) = await ArrangeAsync(code => code.Write("contracts/openapi.yaml", "openapi: 3.1.0\n"));
        await using var scope = host;
        using var client = host.Client();

        var (_, body) = await PostAsync(client, "/v1/evaluations/diff", new { repository = "gateway", baseSha, headSha = head });

        Assert.Equal("REQUIRE_REVIEW", body.GetProperty("verdict").GetString());
        Assert.True(body.GetProperty("significantChange").GetBoolean());
        Assert.Contains("DESIGN_REQUIRED", FindingCodes(body));
        Assert.NotEmpty(body.GetProperty("requiredActions").EnumerateArray());
    }

    [Fact]
    public async Task The_pull_request_gate_names_the_pull_request_and_stores_a_pr_evaluation()
    {
        var (host, baseSha, head) = await ArrangeAsync(code => code.Write("src/Program.cs", "class Program { }\n"));
        await using var scope = host;
        using var client = host.Client();

        var (missing, error) = await PostAsync(client, "/v1/evaluations/pr", new { repository = "gateway", baseSha, headSha = head });
        var (status, body) = await PostAsync(client, "/v1/evaluations/pr", new { repository = "gateway", baseSha, headSha = head, pullRequest = "42" });

        Assert.Equal(400, missing);
        Assert.Equal("INVALID_REQUEST", error.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(200, status);
        Assert.Equal("PR", body.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task An_unknown_commit_is_an_error_never_a_verdict()
    {
        var (host, baseSha, _) = await ArrangeAsync(code => code.Write("src/Program.cs", "class Program { }\n"));
        await using var scope = host;
        using var client = host.Client();

        var (status, body) = await PostAsync(client, "/v1/evaluations/diff", new { repository = "gateway", baseSha, headSha = new string('a', 40) });

        Assert.Equal(503, status);
        Assert.Equal("SCM_UNAVAILABLE", body.GetProperty("error").GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("verdict", out _));
    }

    [Fact]
    public async Task A_repository_on_a_host_that_is_not_allowed_is_never_fetched()
    {
        var (host, baseSha, head) = await ArrangeAsync(code => code.Write("src/Program.cs", "class Program { }\n"), allowLocalScm: false);
        await using var scope = host;
        using var client = host.Client();

        var (status, body) = await PostAsync(client, "/v1/evaluations/diff", new { repository = "gateway", baseSha, headSha = head });

        Assert.Equal(503, status);
        Assert.Equal("SCM_UNAVAILABLE", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Callers_need_the_evaluate_right_and_valid_input()
    {
        var (host, baseSha, head) = await ArrangeAsync(code => code.Write("src/Program.cs", "class Program { }\n"));
        await using var scope = host;
        using var reader = host.Client("reader");
        using var contributor = host.Client();

        var (forbidden, _) = await PostAsync(reader, "/v1/evaluations/diff", new { repository = "gateway", baseSha, headSha = head });
        var (invalid, _) = await PostAsync(contributor, "/v1/evaluations/diff", new { repository = "gateway", baseSha = "not-a-sha", headSha = head });
        var (unknownDesign, _) = await PostAsync(contributor, "/v1/evaluations/diff", new { repository = "gateway", baseSha, headSha = head, designEvaluationId = "ev_missing" });

        Assert.Equal(403, forbidden);
        Assert.Equal(400, invalid);
        Assert.Equal(404, unknownDesign);
    }
}
