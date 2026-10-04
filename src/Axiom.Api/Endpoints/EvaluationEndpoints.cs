using System.Text.Json;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Application.Review;
using Axiom.Domain.Audit;
using Axiom.Domain.Evaluation;

namespace Axiom.Api.Endpoints;

public sealed record HarnessBody(string? Type, string? SessionId);

public sealed record PreflightBody(string? Organization, string Repository, string Ref, string Task, string[]? Paths, string? Environment, HarnessBody? Harness);

public sealed record DesignBody(string? Organization, string Repository, string Ref, JsonElement? Design, string? DesignMarkdown, string? PreflightEvaluationId, HarnessBody? Harness);

public sealed record DiffBody(string? Organization, string Repository, string BaseSha, string HeadSha, string? Ref, string? PullRequest, string? DesignEvaluationId, HarnessBody? Harness);

public sealed record ReviewBody(bool Approve, string Comment);

public sealed record CommentBody(string Comment);

public sealed record ReceiptBody(
    string ReceiptId, string EvaluationId, string Stage, string Verdict, string Repository, string? CommitSha, string SnapshotId,
    DateTimeOffset IssuedAt, string Digest, string PreviousChainDigest, string ChainDigest, bool Verified, JsonElement Payload)
{
    public static ReceiptBody From(Receipt r) => new(
        r.Id, r.EvaluationId, EvaluationResult.StageName(r.Stage), VerdictLattice.ToContract(r.Verdict), r.Repository, r.CommitSha, r.SnapshotId,
        r.IssuedAt, r.Digest, r.PreviousChainDigest, r.ChainDigest, ReceiptFactory.Verify(r), JsonDocument.Parse(r.Payload).RootElement.Clone());
}

public sealed record ChainVerificationBody(bool Intact, long Receipts, string? FirstBrokenReceiptId, string HeadChainDigest);

internal static class EvaluationEndpoints
{
    public static IEndpointRouteBuilder MapEvaluationEndpoints(this IEndpointRouteBuilder routes)
    {
        var evaluations = routes.MapGroup("/v1/evaluations").WithTags("Evaluations");

        evaluations.MapPost("/preflight", async (PreflightBody body, AxiomPrincipal principal, PreflightService service, CancellationToken ct) =>
            EvaluationResult.From(await service.ExecuteAsync(
                principal,
                new PreflightRequest(body.Organization, body.Repository, body.Ref, body.Task, body.Paths, body.Environment, body.Harness?.Type, body.Harness?.SessionId),
                ct)))
            .WithName("preflightChange");

        evaluations.MapPost("/design", async (DesignBody body, AxiomPrincipal principal, DesignValidationService service, CancellationToken ct) =>
            EvaluationResult.From(await service.ExecuteAsync(
                principal,
                new DesignValidationRequest(body.Organization, body.Repository, body.Ref, body.Design, body.DesignMarkdown, body.PreflightEvaluationId, body.Harness?.Type, body.Harness?.SessionId),
                ct)))
            .WithName("validateDesign");

        evaluations.MapPost("/diff", async (DiffBody body, AxiomPrincipal principal, DiffEvaluationService service, CancellationToken ct) =>
            EvaluationResult.From(await service.ExecuteAsync(principal, ToRequest(body), ct)))
            .WithName("validateDiff");

        evaluations.MapPost("/pr", async (DiffBody body, AxiomPrincipal principal, DiffEvaluationService service, CancellationToken ct) =>
            EvaluationResult.From(await service.ExecutePullRequestAsync(principal, ToRequest(body), ct)))
            .WithName("validatePullRequest");

        evaluations.MapGet("/{id}", async (string id, AxiomPrincipal principal, ReviewService reviews, CancellationToken ct) =>
        {
            var state = await reviews.GetAsync(principal, id, ct);
            return new
            {
                evaluation = EvaluationResult.From(state.Stored),
                task = state.Stored.Evaluation.Task,
                actor = state.Stored.Evaluation.Actor,
                scm = state.Stored.Evaluation.Scm,
                trace = state.Stored.Evaluation.Trace,
                clearance = state.Clearance.State.ToString(),
                reviews = state.Decisions,
            };
        }).WithName("getEvaluation");

        evaluations.MapGet("/", async (
            AxiomPrincipal principal, IEvaluationStore store, string? repository, string? stage, string? verdict, string? commitSha, string? governanceId,
            DateTimeOffset? since, int? skip, int? take, CancellationToken ct) =>
        {
            Authorizer.Demand(principal, AccessRight.ReadAudit);
            var (items, total) = await store.SearchAsync(principal.OrganizationId, new EvaluationQuery
            {
                Repository = repository,
                Stage = ParseEnum<EvaluationStage>(stage, "stage"),
                Verdict = ParseEnum<Verdict>(verdict?.Replace("_", string.Empty, StringComparison.Ordinal), "verdict"),
                CommitSha = commitSha,
                GovernanceId = governanceId,
                Since = since,
                Skip = skip ?? 0,
                Take = take ?? 50,
            }, ct);
            return new { total, items };
        }).WithName("searchEvaluations");

        evaluations.MapPost("/{id}/review", async (string id, ReviewBody body, AxiomPrincipal principal, ReviewService reviews, CancellationToken ct) =>
            await reviews.ReviewAsync(principal, new ReviewRequest(id, body.Approve, body.Comment), ct)).WithName("reviewEvaluation");

        evaluations.MapPost("/{id}/findings/{code}/overturn", async (string id, string code, CommentBody body, AxiomPrincipal principal, ReviewService reviews, CancellationToken ct) =>
            await reviews.OverturnFindingAsync(principal, new FindingOverturnRequest(id, code, body.Comment), ct)).WithName("overturnFinding");

        evaluations.MapPost("/{id}/classification-override", async (string id, CommentBody body, AxiomPrincipal principal, ReviewService reviews, CancellationToken ct) =>
            await reviews.OverrideClassificationAsync(principal, id, body.Comment, ct)).WithName("overrideClassification");

        routes.MapGet("/v1/reviews/pending", async (AxiomPrincipal principal, ReviewService reviews, int? skip, int? take, CancellationToken ct) =>
            await reviews.ListPendingAsync(principal, skip ?? 0, take ?? 50, ct)).WithTags("Reviews").WithName("listPendingReviews");

        var receipts = routes.MapGroup("/v1/receipts").WithTags("Receipts");

        receipts.MapGet("/{id}", async (string id, AxiomPrincipal principal, IEvaluationStore store, CancellationToken ct) =>
        {
            Authorizer.Demand(principal, AccessRight.ReadAudit);
            var receipt = await store.FindReceiptAsync(principal.OrganizationId, id, ct) ?? throw AxiomException.NotFound($"Receipt '{id}'");
            return ReceiptBody.From(receipt);
        }).WithName("getReceipt");

        receipts.MapGet("/", async (string repository, string commitSha, string? stage, AxiomPrincipal principal, IEvaluationStore store, CancellationToken ct) =>
        {
            Authorizer.Demand(principal, AccessRight.ReadAudit);
            var receipt = await store.FindReceiptByCommitAsync(principal.OrganizationId, repository, commitSha, ParseEnum<EvaluationStage>(stage, "stage"), ct)
                ?? throw AxiomException.NotFound($"A receipt for {repository}@{commitSha}");
            return ReceiptBody.From(receipt);
        }).WithName("getReceiptByCommit");

        receipts.MapGet("/chain/verify", async (AxiomPrincipal principal, IEvaluationStore store, CancellationToken ct) =>
        {
            Authorizer.Demand(principal, AccessRight.ReadAudit);
            long count = 0;
            var expected = ReceiptFactory.GenesisDigest;
            await foreach (var receipt in store.ReadChainAsync(principal.OrganizationId, ct))
            {
                count++;
                if (!ReceiptFactory.Verify(receipt) || !string.Equals(receipt.PreviousChainDigest, expected, StringComparison.Ordinal))
                {
                    return new ChainVerificationBody(false, count, receipt.Id, expected);
                }

                expected = receipt.ChainDigest;
            }

            return new ChainVerificationBody(true, count, null, expected);
        }).WithName("verifyReceiptChain");

        return routes;
    }

    private static DiffValidationRequest ToRequest(DiffBody body) =>
        new(body.Organization, body.Repository, body.BaseSha, body.HeadSha, body.Ref, body.PullRequest, body.DesignEvaluationId, body.Harness?.Type, body.Harness?.SessionId);

    private static T? ParseEnum<T>(string? value, string name)
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
