using Axiom.Application.Common;
using Axiom.Application.Governance;

namespace Axiom.Api.Endpoints;

public sealed record GovernanceSourceBody(string? RepositoryUrl, string? Branch, string? RootPath);

/// <summary>
/// Onboarding: which Git repository holds the organization's governance records. Only the location is
/// stored, never a credential; private repositories get their token from the deployment's secret store.
/// The workers pick up a change on their next cycle.
/// </summary>
internal static class SourceEndpoints
{
    public static IEndpointRouteBuilder MapSourceEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/admin/governance-source").WithTags("Onboarding");

        group.MapGet("/", async (AxiomPrincipal principal, IGovernanceSourceRegistry registry, IGovernanceSource source, IGovernanceSnapshotProvider snapshots, CancellationToken ct) =>
        {
            Authorizer.Demand(principal, AccessRight.ManageGovernanceSource);
            var config = await registry.FindAsync(principal.OrganizationId, ct);
            return await DescribeAsync(config, source, snapshots, principal.OrganizationId, ct);
        }).WithName("getGovernanceSource");

        group.MapPut("/", async (GovernanceSourceBody body, AxiomPrincipal principal, IGovernanceSourceRegistry registry, IGovernanceSource source, IGovernanceSnapshotProvider snapshots, CancellationToken ct) =>
        {
            Authorizer.Demand(principal, AccessRight.ManageGovernanceSource);
            if (string.IsNullOrWhiteSpace(body.RepositoryUrl))
            {
                throw AxiomException.Invalid("'repositoryUrl' is required.");
            }

            var config = new GovernanceSourceConfig(
                principal.OrganizationId,
                body.RepositoryUrl.Trim(),
                string.IsNullOrWhiteSpace(body.Branch) ? "main" : body.Branch.Trim(),
                body.RootPath?.Trim().Trim('/') ?? string.Empty);

            // Prove the location works before storing it, so a typo fails here and not silently in a worker.
            int files;
            try
            {
                var head = await source.FetchHeadAsync(config, ct);
                files = (await source.ReadTreeAsync(config, head.Sha, ct)).Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw AxiomException.Invalid($"Axiom could not read that repository: {ex.Message} Use the full https clone URL (for example https://github.com/your-org/governance.git); local paths only work when the deployment allows them, and private repositories need a token in the deployment secrets.");
            }

            if (files == 0)
            {
                throw AxiomException.Invalid($"The repository is reachable but holds no files under '{config.RootPath}'. Check the branch and the records directory.");
            }

            await registry.UpsertAsync(config, ct);
            return await DescribeAsync(config, source, snapshots, principal.OrganizationId, ct);
        }).WithName("setGovernanceSource");

        return routes;
    }

    private static async Task<object> DescribeAsync(
        GovernanceSourceConfig? config, IGovernanceSource source, IGovernanceSnapshotProvider snapshots, string organizationId, CancellationToken ct)
    {
        var snapshot = await snapshots.GetCurrentInfoAsync(organizationId, ct);
        if (config is null)
        {
            return new { configured = false, snapshot };
        }

        object? head;
        try
        {
            var h = await source.FetchHeadAsync(config, ct);
            head = new { sha = h.Sha, committedAt = h.CommittedAt, error = (string?)null };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            head = new { sha = (string?)null, committedAt = (DateTimeOffset?)null, error = ex.Message };
        }

        return new { configured = true, source = new { config.RepositoryUrl, config.Branch, config.RootPath }, snapshot, head };
    }
}
