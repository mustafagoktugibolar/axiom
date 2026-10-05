using System.Net.Http.Json;
using System.Text.Json;
using Axiom.IntegrationTests.Support;
using static Axiom.IntegrationTests.Evaluation.ApiHost;

namespace Axiom.IntegrationTests.Evaluation;

/// <summary>Connecting a governance repository through the API: validated before it is stored, admin-only.</summary>
[Collection(PostgresTests.Name)]
public sealed class OnboardingApiTests(PostgresFixture postgres)
{
    private static async Task<(int Status, JsonElement Body)> PutAsync(HttpClient client, object body)
    {
        using var response = await client.PutAsJsonAsync(new Uri("/v1/admin/governance-source", UriKind.Relative), body);
        var json = await response.Content.ReadAsStringAsync();
        return ((int)response.StatusCode, json.Length == 0 ? default : JsonDocument.Parse(json).RootElement.Clone());
    }

    [Fact]
    public async Task A_readable_repository_is_connected_and_reported()
    {
        await using var host = new ApiHost(postgres);
        host.Governance.Write("governance/decisions/ARCH-100.yaml", GovernanceFixtures.GeneratedCodeDecision()).Commit("governance");
        using var admin = host.Client("platform-admin");

        var (before, empty) = await GetAsync(admin, "/v1/admin/governance-source");
        Assert.Equal(200, before);
        Assert.False(empty.GetProperty("configured").GetBoolean());

        var (status, body) = await PutAsync(admin, new { repositoryUrl = host.Governance.Location, branch = host.Governance.Branch, rootPath = "governance" });

        Assert.Equal(200, status);
        Assert.True(body.GetProperty("configured").GetBoolean());
        Assert.Equal("governance", body.GetProperty("source").GetProperty("rootPath").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("head").GetProperty("sha").GetString()));
    }

    [Fact]
    public async Task An_unreadable_repository_or_an_empty_records_directory_is_rejected_and_nothing_is_stored()
    {
        await using var host = new ApiHost(postgres);
        host.Governance.Write("README.md", "# nothing here\n").Commit("readme");
        using var admin = host.Client("platform-admin");

        var (missing, _) = await PutAsync(admin, new { repositoryUrl = Path.Combine(host.Governance.Location, "does-not-exist"), branch = "main", rootPath = "governance" });
        var (empty, _) = await PutAsync(admin, new { repositoryUrl = host.Governance.Location, branch = host.Governance.Branch, rootPath = "governance" });

        Assert.Equal(400, missing);
        Assert.Equal(400, empty);
        var (_, state) = await GetAsync(admin, "/v1/admin/governance-source");
        Assert.False(state.GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task Only_a_platform_admin_may_connect_a_repository()
    {
        await using var host = new ApiHost(postgres);
        host.Governance.Write("governance/decisions/ARCH-100.yaml", GovernanceFixtures.GeneratedCodeDecision()).Commit("governance");
        using var contributor = host.Client("contributor");

        var (status, _) = await PutAsync(contributor, new { repositoryUrl = host.Governance.Location, branch = host.Governance.Branch, rootPath = "governance" });

        Assert.Equal(403, status);
    }
}
