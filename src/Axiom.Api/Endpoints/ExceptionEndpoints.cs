using Axiom.Application.Common;
using Axiom.Application.Exceptions;

namespace Axiom.Api.Endpoints;

public sealed record ExceptionRequestBody(
    string[]? Targets, Dictionary<string, string[]>? Scope, string? Title, string? Rationale, DateTimeOffset? StartsAt, DateTimeOffset ExpiresAt,
    string? TrackingIssue, string[]? CompensatingControls);

public sealed record ExceptionDecisionBody(bool Approve, string Comment);

internal static class ExceptionEndpoints
{
    public static IEndpointRouteBuilder MapExceptionEndpoints(this IEndpointRouteBuilder routes)
    {
        var exceptions = routes.MapGroup("/v1/exceptions/requests").WithTags("Exceptions");

        exceptions.MapPost("/", async (ExceptionRequestBody body, AxiomPrincipal principal, ExceptionRequestService service, CancellationToken ct) =>
        {
            var created = await service.RequestAsync(
                principal, new ExceptionRequestInput(body.Targets, body.Scope, body.Title, body.Rationale, body.StartsAt, body.ExpiresAt, body.TrackingIssue, body.CompensatingControls), ct);
            return Results.Accepted($"/v1/exceptions/requests/{created.Id}", created);
        }).WithName("requestException");

        exceptions.MapGet("/", async (AxiomPrincipal principal, ExceptionRequestService service, bool? mine, string? status, int? skip, int? take, CancellationToken ct) =>
        {
            ExceptionRequestStatus? parsed = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                parsed = Enum.TryParse<ExceptionRequestStatus>(status, ignoreCase: true, out var s) && Enum.IsDefined(s) ? s : throw AxiomException.Invalid($"'{status}' is not a valid status.");
            }

            var (items, total) = await service.ListAsync(principal, mine ?? false, parsed, skip ?? 0, take ?? 50, ct);
            return new { total, items };
        }).WithName("listExceptionRequests");

        exceptions.MapGet("/{id}", async (string id, AxiomPrincipal principal, ExceptionRequestService service, CancellationToken ct) =>
            await service.GetAsync(principal, id, ct)).WithName("getExceptionRequest");

        exceptions.MapPost("/{id}/decision", async (string id, ExceptionDecisionBody body, AxiomPrincipal principal, ExceptionRequestService service, CancellationToken ct) =>
            await service.DecideAsync(principal, id, body.Approve, body.Comment, ct)).WithName("decideExceptionRequest");

        return routes;
    }
}
