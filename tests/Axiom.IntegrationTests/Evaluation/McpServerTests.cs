using System.Text.Json;
using Axiom.IntegrationTests.Support;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Axiom.IntegrationTests.Evaluation;

/// <summary>The MCP surface end to end: a real MCP client over HTTP against the real host, Git repositories and PostgreSQL (ADR-0003).</summary>
[Collection(PostgresTests.Name)]
public sealed class McpServerTests(PostgresFixture postgres)
{
    private static readonly string[] ExpectedTools =
    [
        "governance.explain_finding", "governance.get_context", "governance.get_decision", "governance.get_receipt", "governance.impact_analysis",
        "governance.preflight_change", "governance.query_decisions", "governance.validate_design", "governance.validate_diff",
    ];

    private async Task<(ApiHost Host, string Base, string Head)> ArrangeAsync(string violatingPath = "src/Generated/Client.cs")
    {
        var host = new ApiHost(postgres);
        host.Governance.Write("governance/decisions/ARCH-100.yaml", GovernanceFixtures.GeneratedCodeDecision()).Commit("governance");
        host.Code.Write("src/Program.cs", "class Program;\n");
        var baseSha = host.Code.Commit("base");
        host.Code.Write(violatingPath, "class Client;\n");
        var head = host.Code.Commit("change");
        await host.SeedAsync();
        return (host, baseSha, head);
    }

    private static async Task<McpClient> ConnectAsync(ApiHost host, params string[] roles)
    {
        var http = host.Client(roles);
        var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") }, http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private static JsonElement Payload(CallToolResult result)
    {
        var text = Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new Xunit.Sdk.XunitException("Tool result is not JSON: " + text);
        }
    }

    [Fact]
    public async Task The_server_publishes_every_governance_tool_with_a_schema_and_a_description()
    {
        var (host, _, _) = await ArrangeAsync();
        await using var scope = host;
        await using var client = await ConnectAsync(host);

        var tools = await client.ListToolsAsync();

        Assert.Equal(ExpectedTools, tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.All(tools, tool =>
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.Equal(JsonValueKind.Object, tool.JsonSchema.ValueKind);
        });
        var diff = tools.Single(t => t.Name == "governance.validate_diff");
        Assert.Equal(["baseSha", "headSha", "repository"], diff.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Validate_diff_returns_the_same_evaluation_shape_as_rest()
    {
        var (host, baseSha, head) = await ArrangeAsync();
        await using var scope = host;
        await using var client = await ConnectAsync(host);

        var result = await client.CallToolAsync("governance.validate_diff", new Dictionary<string, object?> { ["repository"] = "gateway", ["baseSha"] = baseSha, ["headSha"] = head });

        Assert.NotEqual(true, result.IsError);
        var body = Payload(result);
        Assert.Equal("BLOCK", body.GetProperty("verdict").GetString());
        Assert.Equal("DIFF", body.GetProperty("stage").GetString());
        Assert.Contains(body.GetProperty("findings").EnumerateArray(), f => f.GetProperty("code").GetString() == "POLICY_VIOLATION" && f.GetProperty("path").GetString() == "src/Generated/Client.cs");

        using var rest = host.Client();
        var (_, viaRest) = await ApiHost.PostAsync(rest, "/v1/evaluations/diff", new { repository = "gateway", baseSha, headSha = head });
        Assert.Equal(viaRest.GetProperty("evaluationId").GetString(), body.GetProperty("evaluationId").GetString());
    }

    [Fact]
    public async Task A_failure_is_a_tool_error_in_the_contract_shape_never_a_verdict()
    {
        var (host, baseSha, _) = await ArrangeAsync();
        await using var scope = host;
        await using var client = await ConnectAsync(host);

        var result = await client.CallToolAsync("governance.validate_diff", new Dictionary<string, object?> { ["repository"] = "gateway", ["baseSha"] = baseSha, ["headSha"] = new string('a', 40) });

        Assert.True(result.IsError);
        var error = Payload(result).GetProperty("error");
        Assert.Equal("SCM_UNAVAILABLE", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("retryable").GetBoolean());
        Assert.False(Payload(result).TryGetProperty("verdict", out _));

        var invalid = await client.CallToolAsync("governance.validate_diff", new Dictionary<string, object?> { ["repository"] = "gateway", ["baseSha"] = "nope", ["headSha"] = baseSha });
        Assert.True(invalid.IsError);
        Assert.Equal("INVALID_REQUEST", Payload(invalid).GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_caller_is_authorized_per_tool_call()
    {
        var (host, baseSha, head) = await ArrangeAsync();
        await using var scope = host;
        await using var reader = await ConnectAsync(host, "reader");

        var denied = await reader.CallToolAsync("governance.validate_diff", new Dictionary<string, object?> { ["repository"] = "gateway", ["baseSha"] = baseSha, ["headSha"] = head });
        var allowed = await reader.CallToolAsync("governance.query_decisions", new Dictionary<string, object?> { ["text"] = "generated" });

        Assert.True(denied.IsError);
        Assert.Equal("FORBIDDEN", Payload(denied).GetProperty("error").GetProperty("code").GetString());
        Assert.NotEqual(true, allowed.IsError);
    }

    [Fact]
    public async Task Preflight_context_decisions_and_impact_work_over_mcp()
    {
        var (host, _, _) = await ArrangeAsync();
        await using var scope = host;
        await using var client = await ConnectAsync(host, "contributor", "auditor");

        var preflight = Payload(await client.CallToolAsync("governance.preflight_change", new Dictionary<string, object?>
        {
            ["repository"] = "gateway", ["ref"] = "feature/x", ["task"] = "Add a health endpoint", ["paths"] = new[] { "src/Health.cs" },
        }));
        var evaluationId = preflight.GetProperty("evaluationId").GetString()!;
        Assert.Equal("PRE_FLIGHT", preflight.GetProperty("stage").GetString());
        Assert.Equal(["ARCH-100"], preflight.GetProperty("applicableGovernance").EnumerateArray().Select(a => a.GetProperty("id").GetString()));
        Assert.Equal("forbidden-path-change", Assert.Single(preflight.GetProperty("requiredChecks").EnumerateArray()).GetProperty("ruleId").GetString());

        var bundle = Payload(await client.CallToolAsync("governance.get_context", new Dictionary<string, object?> { ["evaluationId"] = evaluationId, ["includeRationale"] = true }));
        var decision = Assert.Single(bundle.GetProperty("decisions").EnumerateArray()).GetProperty("record");
        Assert.Equal("ARCH-100", decision.GetProperty("id").GetString());
        Assert.True(decision.GetProperty("authoritative").GetBoolean());
        Assert.Equal("forbidden-path-change", Assert.Single(decision.GetProperty("rules").EnumerateArray()).GetProperty("ruleId").GetString());

        var found = Payload(await client.CallToolAsync("governance.query_decisions", new Dictionary<string, object?> { ["repository"] = "gateway", ["status"] = new[] { "accepted" } }));
        Assert.Equal(1, found.GetProperty("total").GetInt32());
        Assert.True(Assert.Single(found.GetProperty("items").EnumerateArray()).GetProperty("isAuthoritative").GetBoolean());

        var detail = Payload(await client.CallToolAsync("governance.get_decision", new Dictionary<string, object?> { ["id"] = "ARCH-100" }));
        Assert.Equal("Generated code is not edited by hand", detail.GetProperty("record").GetProperty("title").GetString());
        Assert.Single(detail.GetProperty("history").EnumerateArray());

        var impact = Payload(await client.CallToolAsync("governance.impact_analysis", new Dictionary<string, object?> { ["repository"] = "gateway" }));
        Assert.Equal("repo:gateway", impact.GetProperty("change").GetString());
        Assert.Equal(["ARCH-100"], impact.GetProperty("relatedGovernance").EnumerateArray().Select(i => i.GetString()));
    }

    [Fact]
    public async Task Receipts_and_finding_explanations_trace_a_verdict_to_its_evidence()
    {
        var (host, baseSha, head) = await ArrangeAsync();
        await using var scope = host;
        await using var client = await ConnectAsync(host, "contributor", "auditor");
        var evaluation = Payload(await client.CallToolAsync("governance.validate_diff", new Dictionary<string, object?> { ["repository"] = "gateway", ["baseSha"] = baseSha, ["headSha"] = head }));
        var evaluationId = evaluation.GetProperty("evaluationId").GetString()!;

        var receipt = Payload(await client.CallToolAsync("governance.get_receipt", new Dictionary<string, object?> { ["repository"] = "gateway", ["commitSha"] = head }));
        Assert.Equal(evaluationId, receipt.GetProperty("evaluationId").GetString());
        Assert.True(receipt.GetProperty("verified").GetBoolean());
        Assert.Equal(receipt.GetProperty("receiptId").GetString(), Payload(await client.CallToolAsync("governance.get_receipt", new Dictionary<string, object?> { ["id"] = evaluationId })).GetProperty("receiptId").GetString());

        var explanation = Payload(await client.CallToolAsync("governance.explain_finding", new Dictionary<string, object?> { ["evaluationId"] = evaluationId, ["code"] = "POLICY_VIOLATION" }));
        Assert.Equal("src/Generated/Client.cs", explanation.GetProperty("finding").GetProperty("path").GetString());
        Assert.Equal("forbidden-path-change", Assert.Single(explanation.GetProperty("selectedRules").EnumerateArray()).GetProperty("ruleId").GetString());
        Assert.Equal(["platform-architecture"], Assert.Single(explanation.GetProperty("authorityOwners").EnumerateArray()).GetProperty("owners").EnumerateArray().Select(o => o.GetString()));
        Assert.Contains(explanation.GetProperty("remediationOptions").EnumerateArray(), o => o.GetString()!.Contains("exception", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(explanation.GetProperty("resolutionTrace").EnumerateArray());
    }

    [Fact]
    public async Task Unauthenticated_calls_are_challenged_with_the_resource_metadata_location()
    {
        var (host, _, _) = await ArrangeAsync();
        await using var scope = host;
        using var anonymous = host.AnonymousClient();

        using var challenge = await anonymous.PostAsync(new Uri("/mcp", UriKind.Relative), new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        using var metadata = await anonymous.GetAsync(new Uri("/.well-known/oauth-protected-resource", UriKind.Relative));

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, challenge.StatusCode);
        Assert.Contains("resource_metadata=\"http://localhost/.well-known/oauth-protected-resource\"", string.Join(' ', challenge.Headers.WwwAuthenticate.Select(h => h.ToString())), StringComparison.Ordinal);
        Assert.Equal(System.Net.HttpStatusCode.OK, metadata.StatusCode);
        var document = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("http://localhost/mcp", document.GetProperty("resource").GetString());
        Assert.Contains("axiom.evaluate", document.GetProperty("scopes_supported").EnumerateArray().Select(s => s.GetString()));
    }
}
