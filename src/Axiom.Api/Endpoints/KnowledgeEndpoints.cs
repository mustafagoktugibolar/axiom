using Axiom.Application.Candidates;
using Axiom.Application.Common;
using Axiom.Application.Coverage;
using Axiom.Domain.Governance;

namespace Axiom.Api.Endpoints;

public sealed record AdrImportBody(string? Repository, string? Branch);

public sealed record DocsScanBody(string? Repository, string? Branch);

public sealed record ReviewMiningBody(string? Repository, int? MinOccurrences, int? MinPullRequests, int? MaxPullRequests);

public sealed record PostmortemImportBody(string? Repository, string? Branch, string? Path);

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

        // Reads a registered repository through the SCM adapter (same host rules as diffs) and stores its ADRs as candidates.
        routes.MapPost("/v1/admin/candidates/import-adrs", async (AdrImportBody body, AxiomPrincipal principal, AdrImportService service, CancellationToken ct) =>
            await service.ImportRepositoryAsync(principal, body.Repository ?? string.Empty, body.Branch, ct))
            .WithTags("Candidates").WithName("importAdrs");

        // Candidate sources 11.2 to 11.4: they only ever store proposed candidates; same authorization as the ADR import.
        routes.MapPost("/v1/admin/candidates/scan-docs", async (DocsScanBody body, AxiomPrincipal principal, DocsScanService service, CancellationToken ct) =>
            await service.ScanRepositoryAsync(principal, body.Repository ?? string.Empty, body.Branch, ct))
            .WithTags("Candidates").WithName("scanDocsForCandidates");

        routes.MapPost("/v1/admin/candidates/mine-review-comments", async (ReviewMiningBody body, AxiomPrincipal principal, ReviewCommentMiningService service, CancellationToken ct) =>
            await service.MineAsync(principal, body.Repository ?? string.Empty, body.MinOccurrences, body.MinPullRequests, body.MaxPullRequests, ct))
            .WithTags("Candidates").WithName("mineReviewComments");

        routes.MapPost("/v1/admin/candidates/import-postmortems", async (PostmortemImportBody body, AxiomPrincipal principal, PostmortemImportService service, CancellationToken ct) =>
            await service.ImportRepositoryAsync(principal, body.Repository ?? string.Empty, body.Branch, body.Path, ct))
            .WithTags("Candidates").WithName("importPostmortems");

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
