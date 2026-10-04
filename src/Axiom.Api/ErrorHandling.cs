using Axiom.Application.Common;
using Microsoft.AspNetCore.Diagnostics;

namespace Axiom.Api;

/// <summary>Error body shared with the MCP contract: failure is never shaped like a verdict.</summary>
public sealed record ErrorBody(ErrorDetail Error);

public sealed record ErrorDetail(string Code, string Message, bool Retryable, string? TraceId);

/// <summary>
/// Maps application failures to HTTP. Anything unexpected becomes 503 GOVERNANCE_UNAVAILABLE so that a
/// caller can never mistake an internal failure for governance success.
/// </summary>
internal sealed partial class AxiomExceptionHandler(ILogger<AxiomExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var traceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;
        var (status, detail) = exception switch
        {
            AxiomException e => (StatusOf(e.Kind), new ErrorDetail(e.Code, e.Message, e.Retryable, traceId)),
            BadHttpRequestException or System.Text.Json.JsonException =>
                (StatusCodes.Status400BadRequest, new ErrorDetail(ErrorCodes.InvalidRequest, "The request body is not valid.", false, traceId)),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested =>
                (StatusCodes.Status499ClientClosedRequest, new ErrorDetail(ErrorCodes.GovernanceUnavailable, "The request was cancelled.", true, traceId)),
            _ => (StatusCodes.Status503ServiceUnavailable,
                new ErrorDetail(ErrorCodes.GovernanceUnavailable, "Axiom could not complete the request. No governance verdict was produced.", true, traceId)),
        };

        if (status >= 500)
        {
            LogFailure(logger, exception, detail.Code);
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(new ErrorBody(detail), cancellationToken);
        return true;
    }

    private static int StatusOf(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status503ServiceUnavailable,
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Request failed with {Code}")]
    private static partial void LogFailure(ILogger logger, Exception exception, string code);
}
