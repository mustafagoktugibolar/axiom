using Axiom.Application.Candidates;
using Axiom.Application.Common;
using Axiom.Application.Coverage;
using Axiom.Domain.Governance;

namespace Axiom.Api.Endpoints;

public sealed record AdrImportBody(string? Repository, string? Branch);

public sealed record CandidateDecisionBody(bool Approve, string? Comment);

/// <summary>Knowledge onboarding: candidates (never authoritative), ADR import and governance coverage.</summary>
internal static class KnowledgeEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeEndpoints(this IEndpointRouteBuilder routes)
    {
        var candidates = routes.MapGroup("/v1/candidates").WithTags("Candidates");

        candidates.MapGet("/", async (AxiomPrincipal principal, CandidateService service, string? state, string? kind, string? owner, int? skip, int? take, CancellationToken ct) =>
        {
            var (items, total) = await service.ListAsync(
                principal, new CandidateListQuery(Parse<CandidateState>(state, "state"), Parse<RecordKind>(kind, "kind"), owner, skip ?? 0, take ?? 50), ct);
            return new { total, items };
        }).WithName("listCandidates");

        candidates.MapGet("/{id}", async (string id, AxiomPrincipal principal, CandidateService service, CancellationToken ct) =>
            await service.GetAsync(principal, id, ct)).WithName("getCandidate");

        // The review queue's decision. Approving never makes the candidate govern; only the merged record does.
        candidates.MapPost("/{id}/decision", async (string id, CandidateDecisionBody body, AxiomPrincipal principal, CandidateService service, CancellationToken ct) =>
            await service.DecideAsync(principal, id, body.Approve, body.Comment, ct)).WithName("decideCandidate");

        // Reads a registered repository through the SCM adapter (same host rules as diffs) and stores its ADRs as candidates.
        routes.MapPost("/v1/admin/candidates/import-adrs", async (AdrImportBody body, AxiomPrincipal principal, AdrImportService service, CancellationToken ct) =>
            await service.ImportRepositoryAsync(principal, body.Repository ?? string.Empty, body.Branch, ct))
            .WithTags("Candidates").WithName("importAdrs");

        routes.MapGet("/v1/coverage", async (AxiomPrincipal principal, GovernanceCoverageService service, string? filter, int? skip, int? take, CancellationToken ct) =>
            await service.GetAsync(principal, Parse<CoverageFilter>(filter, "filter") ?? CoverageFilter.All, skip ?? 0, take ?? GovernanceCoverageService.DefaultPageSize, ct))
            .WithTags("Coverage").WithName("getGovernanceCoverage");

        return routes;
    }

    private static T? Parse<T>(string? value, string name)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Enum.TryParse<T>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw AxiomException.Invalid($"'{value}' is not a valid {name}.");
    }
}
