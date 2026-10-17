using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Application.Governance;
using Axiom.Application.Query;
using Axiom.Domain.Governance;

namespace Axiom.Api.Endpoints;

public sealed record ImpactBody(string? Repository, string? ChangeKind, string? EntityRef, int? Depth, bool IncludeUnconfirmed);

internal static class GovernanceEndpoints
{
    public static IEndpointRouteBuilder MapGovernanceEndpoints(this IEndpointRouteBuilder routes)
    {
        var governance = routes.MapGroup("/v1/governance").WithTags("Governance");

        governance.MapGet("/", async (
            AxiomPrincipal principal, GovernanceReadService service, string? q, string? repository, string? status, string? kind, string? owner, string? tag,
            string? relatedTo, bool? staleOnly, int? skip, int? take, CancellationToken ct) =>
            await service.SearchAsync(
                principal,
                new GovernanceQuery
                {
                    Text = q,
                    Owner = owner,
                    Tag = tag,
                    RelatedTo = relatedTo,
                    StaleOnly = staleOnly ?? false,
                    Statuses = ParseList<LifecycleStatus>(status, "status"),
                    Kinds = ParseList<RecordKind>(kind, "kind"),
                    Scope = string.IsNullOrWhiteSpace(repository)
                        ? ImmutableDictionary<ScopeDimension, string>.Empty
                        : ImmutableDictionary<ScopeDimension, string>.Empty.Add(ScopeDimension.Repository, repository.Trim()),
                    Skip = skip ?? 0,
                    Take = take ?? 50,
                },
                ct)).WithName("searchGovernance");

        governance.MapGet("/graph", async (
            AxiomPrincipal principal, GovernanceGraphService service, string? focus, int? depth, int? maxNodes, int? maxEdges, string? kind, string? status, string? scope,
            CancellationToken ct) =>
            await service.GetAsync(
                principal,
                new GovernanceGraphRequest
                {
                    Focus = focus,
                    Depth = depth,
                    MaxNodes = maxNodes,
                    MaxEdges = maxEdges,
                    Kinds = ParseList<RecordKind>(kind, "kind"),
                    Statuses = ParseList<LifecycleStatus>(status, "status"),
                    Scope = ParseScope(scope),
                },
                ct)).WithName("getGovernanceGraph");

        governance.MapGet("/{id}", async (string id, bool? includeRationale, AxiomPrincipal principal, GovernanceReadService service, CancellationToken ct) =>
            await service.GetAsync(principal, id, includeRationale ?? false, ct)).WithName("getGovernanceRecord");

        routes.MapGet("/v1/health/governance", async (AxiomPrincipal principal, GovernanceHealthService service, int? skip, int? take, CancellationToken ct) =>
            await service.GetAsync(principal, skip ?? 0, take ?? 50, ct)).WithTags("Governance").WithName("getGovernanceHealth");

        routes.MapPost("/v1/impact", async (ImpactBody body, AxiomPrincipal principal, ImpactService service, CancellationToken ct) =>
            await service.AnalyzeAsync(principal, new ImpactRequestDto(body.Repository, body.ChangeKind, body.EntityRef, body.Depth, body.IncludeUnconfirmed), ct))
            .WithTags("Impact").WithName("impactAnalysis");

        routes.MapGet("/v1/context/{evaluationId}", async (string evaluationId, bool? includeRationale, AxiomPrincipal principal, ContextService service, CancellationToken ct) =>
            await service.GetAsync(principal, new ContextRequest(evaluationId, null, null, null, null, includeRationale ?? false, null, null), ct))
            .WithTags("Context").WithName("getContext");

        routes.MapGet("/v1/evaluations/{id}/findings/{code}", async (string id, string code, string? path, AxiomPrincipal principal, FindingExplanationService service, CancellationToken ct) =>
            await service.ExplainAsync(principal, id, code, path, ct)).WithTags("Evaluations").WithName("explainFinding");

        return routes;
    }

    /// <summary>Parses "dimension:value" (for example "repository:gateway").</summary>
    private static ImmutableDictionary<ScopeDimension, string> ParseScope(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ImmutableDictionary<ScopeDimension, string>.Empty;
        }

        var parts = value.Split(':', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && parts[1].Length > 0 && Enum.TryParse<ScopeDimension>(parts[0], ignoreCase: true, out var dimension) && Enum.IsDefined(dimension)
            ? ImmutableDictionary<ScopeDimension, string>.Empty.Add(dimension, parts[1])
            : throw AxiomException.Invalid("'scope' must look like 'repository:gateway'.");
    }

    private static ImmutableArray<T> ParseList<T>(string? value, string name)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v =>
            Enum.TryParse<T>(v, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw AxiomException.Invalid($"'{v}' is not a valid {name}."))];
    }
}
