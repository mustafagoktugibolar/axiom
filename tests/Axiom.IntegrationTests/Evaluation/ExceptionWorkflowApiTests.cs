using System.Text.Json;
using Axiom.IntegrationTests.Support;
using static Axiom.IntegrationTests.Evaluation.ApiHost;

namespace Axiom.IntegrationTests.Evaluation;

/// <summary>The exception workflow end to end: request, approval, and the only way an exception takes effect (R10, ADR-0006).</summary>
[Collection(PostgresTests.Name)]
public sealed class ExceptionWorkflowApiTests(PostgresFixture postgres)
{
    private static object Request(int days = 60) => new
    {
        targets = new[] { "ARCH-100" },
        scope = new Dictionary<string, string[]> { ["repositories"] = ["gateway"], ["paths"] = ["src/Generated/**"] },
        rationale = "Generated client is vendored by hand until the generator supports this API.",
        trackingIssue = "GUI-912",
        compensatingControls = new[] { "Regenerate and diff on every release." },
        expiresAt = DateTimeOffset.UtcNow.AddDays(days),
    };

    [Fact]
    public async Task Approval_grants_nothing_until_the_accepted_record_is_merged_into_governance()
    {
        await using var host = new ApiHost(postgres);
        host.Governance.Write("governance/decisions/ARCH-100.yaml", GovernanceFixtures.GeneratedCodeDecision()).Commit("governance");
        host.Code.Write("src/Program.cs", "class Program;\n");
        var baseSha = host.Code.Commit("base");
        host.Code.Write("src/Generated/Client.cs", "class Client;\n");
        var head = host.Code.Commit("change");
        await host.SeedAsync();

        using var alice = host.ClientFor("user:alice", ["gui-platform"], "contributor");
        using var bob = host.ClientFor("user:bob", ["platform-architecture"], "exception-approver");
        var diff = new { repository = "gateway", baseSha, headSha = head };

        // 1. The change violates ARCH-100.
        Assert.Equal("BLOCK", (await PostAsync(alice, "/v1/evaluations/diff", diff)).Body.GetProperty("verdict").GetString());

        // 2. Alice requests an exception; the identical request is the same request.
        var body = Request();
        var (status, created) = await PostAsync(alice, "/v1/exceptions/requests", body);
        var (_, again) = await PostAsync(alice, "/v1/exceptions/requests", body);
        Assert.Equal(202, status);
        Assert.Equal("Pending", created.GetProperty("status").GetString());
        Assert.Equal(["platform-architecture"], created.GetProperty("requiredApprovers").EnumerateArray().Select(a => a.GetString()));
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal(id, again.GetProperty("id").GetString());

        // 3. Visibility: the requester and approvers, nobody else.
        using var mallory = host.ClientFor("user:mallory", ["other"], "contributor");
        Assert.Equal(200, (await GetAsync(alice, $"/v1/exceptions/requests/{id}")).Status);
        Assert.Equal(403, (await GetAsync(mallory, $"/v1/exceptions/requests/{id}")).Status);
        Assert.Equal(1, (await GetAsync(bob, "/v1/exceptions/requests?status=pending")).Body.GetProperty("total").GetInt32());
        Assert.Equal(1, (await GetAsync(alice, "/v1/exceptions/requests?mine=true")).Body.GetProperty("total").GetInt32());

        // 4. Only the routed owners decide, once, and not the requester.
        Assert.Equal(403, (await PostAsync(alice, $"/v1/exceptions/requests/{id}/decision", new { approve = true, comment = "Approving my own request." })).Status);
        var (decided, approved) = await PostAsync(bob, $"/v1/exceptions/requests/{id}/decision", new { approve = true, comment = "Reviewed; the compensating control is adequate." });
        Assert.Equal(200, decided);
        Assert.Equal("Approved", approved.GetProperty("status").GetString());
        Assert.Equal(409, (await PostAsync(bob, $"/v1/exceptions/requests/{id}/decision", new { approve = false, comment = "Second thoughts on this one." })).Status);
        Assert.Equal("Approved", (await GetAsync(alice, $"/v1/exceptions/requests/{id}")).Body.GetProperty("status").GetString());

        // 5. Approval alone waives nothing: authority lives in Git.
        var stillBlocked = (await PostAsync(alice, "/v1/evaluations/diff", diff)).Body;
        Assert.Equal("BLOCK", stillBlocked.GetProperty("verdict").GetString());
        Assert.Empty(stillBlocked.GetProperty("activeExceptions").EnumerateArray());

        // 6. The accepted record is committed to the governance repository and published.
        host.Governance.Write(approved.GetProperty("draftRecordPath").GetString()!, approved.GetProperty("draftRecord").GetString()!).Commit("Grant exception");
        await host.SyncGovernanceAsync();

        // 7. Now it waives, inside its scope, and stays visible.
        var waived = (await PostAsync(alice, "/v1/evaluations/diff", diff)).Body;
        Assert.Equal("ALLOW", waived.GetProperty("verdict").GetString());
        var exception = Assert.Single(waived.GetProperty("activeExceptions").EnumerateArray());
        Assert.StartsWith("EXC-REQ-", exception.GetProperty("id").GetString(), StringComparison.Ordinal);
        var finding = Assert.Single(waived.GetProperty("findings").EnumerateArray(), f => f.GetProperty("code").GetString() == "POLICY_VIOLATION");
        Assert.Equal(exception.GetProperty("id").GetString(), finding.GetProperty("waivedBy").GetString());
    }

    [Fact]
    public async Task Invalid_requests_are_rejected_and_a_rejection_records_the_reason()
    {
        await using var host = new ApiHost(postgres);
        host.Governance.Write("governance/decisions/ARCH-100.yaml", GovernanceFixtures.GeneratedCodeDecision()).Commit("governance");
        await host.SeedAsync();
        using var alice = host.ClientFor("user:alice", ["gui-platform"], "contributor");
        using var bob = host.ClientFor("user:bob", ["platform-architecture"], "exception-approver");
        using var reader = host.ClientFor("user:rita", [], "reader");

        Assert.Equal(400, (await PostAsync(alice, "/v1/exceptions/requests", Request(days: 400))).Status);
        Assert.Equal(400, (await PostAsync(alice, "/v1/exceptions/requests", new { targets = new[] { "ARCH-100" }, scope = new Dictionary<string, string[]>(), rationale = "A blanket waiver, long enough text.", trackingIssue = "X-1", expiresAt = DateTimeOffset.UtcNow.AddDays(5) })).Status);
        Assert.Equal(400, (await PostAsync(alice, "/v1/exceptions/requests", new { targets = new[] { "ARCH-100" }, scope = new Dictionary<string, string[]> { ["repositories"] = ["billing"] }, rationale = "Outside of the target's own scope.", trackingIssue = "X-1", expiresAt = DateTimeOffset.UtcNow.AddDays(5) })).Status);
        Assert.Equal(403, (await PostAsync(reader, "/v1/exceptions/requests", Request())).Status);

        var (_, created) = await PostAsync(alice, "/v1/exceptions/requests", Request());
        var id = created.GetProperty("id").GetString()!;
        var (_, rejected) = await PostAsync(bob, $"/v1/exceptions/requests/{id}/decision", new { approve = false, comment = "Fix the generator instead of editing by hand." });

        Assert.Equal("Rejected", rejected.GetProperty("status").GetString());
        Assert.Equal("Fix the generator instead of editing by hand.", rejected.GetProperty("decision").GetProperty("comment").GetString());
        Assert.Equal("proposed", JsonDocument.Parse(JsonSerializer.Serialize(rejected)).RootElement.GetProperty("draftRecord").GetString()!.Split('\n').Single(l => l.Contains("status:", StringComparison.Ordinal)).Split(':')[1].Trim());
    }

    [Fact]
    public async Task The_mcp_tool_requests_an_exception_and_reports_its_status()
    {
        await using var host = new ApiHost(postgres);
        host.Governance.Write("governance/decisions/ARCH-100.yaml", GovernanceFixtures.GeneratedCodeDecision()).Commit("governance");
        await host.SeedAsync();
        var http = host.ClientFor("user:alice", ["gui-platform"], "contributor");
        var transport = new ModelContextProtocol.Client.HttpClientTransport(
            new ModelContextProtocol.Client.HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") }, http, ownsHttpClient: true);
        await using var client = await ModelContextProtocol.Client.McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync("governance.request_exception", new Dictionary<string, object?>
        {
            ["targets"] = new[] { "ARCH-100" },
            ["scope"] = new Dictionary<string, string[]> { ["repositories"] = ["gateway"], ["paths"] = ["src/Generated/**"] },
            ["rationale"] = "Generated client is vendored by hand until the generator supports this API.",
            ["expiresAt"] = DateTimeOffset.UtcNow.AddDays(30),
            ["trackingIssue"] = "GUI-912",
        });

        Assert.NotEqual(true, result.IsError);
        var body = JsonDocument.Parse(((ModelContextProtocol.Protocol.TextContentBlock)Assert.Single(result.Content)).Text).RootElement;
        Assert.Equal("Pending", body.GetProperty("status").GetString());
        var status = await client.CallToolAsync("governance.get_exception_request", new Dictionary<string, object?> { ["id"] = body.GetProperty("id").GetString() });
        Assert.Equal("Pending", JsonDocument.Parse(((ModelContextProtocol.Protocol.TextContentBlock)Assert.Single(status.Content)).Text).RootElement.GetProperty("status").GetString());

        var invalid = await client.CallToolAsync("governance.request_exception", new Dictionary<string, object?>
        {
            ["targets"] = new[] { "ARCH-404" }, ["scope"] = new Dictionary<string, string[]> { ["repositories"] = ["gateway"] },
            ["rationale"] = "Target does not exist, so this must fail.", ["expiresAt"] = DateTimeOffset.UtcNow.AddDays(30), ["trackingIssue"] = "X-1",
        });
        Assert.True(invalid.IsError);
        Assert.Equal("INVALID_REQUEST", JsonDocument.Parse(((ModelContextProtocol.Protocol.TextContentBlock)Assert.Single(invalid.Content)).Text).RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
