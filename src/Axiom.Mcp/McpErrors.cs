using System.Text.Json;
using Axiom.Application.Common;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace Axiom.Mcp;

/// <summary>
/// Error semantics of the MCP contract: a failure is never shaped like a verdict. Every failure is
/// reported as a tool error whose text is <c>{"error":{"code","retryable","message"}}</c>, and anything
/// unexpected becomes a retryable GOVERNANCE_UNAVAILABLE so an agent cannot read it as ALLOW.
/// </summary>
internal static class McpErrors
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<T> RunAsync<T>(ILogger logger, string tool, Func<Task<T>> work)
    {
        try
        {
            return await work();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AxiomException ex)
        {
            throw Failure(ex.Code, ex.Retryable, ex.Message);
        }
#pragma warning disable CA1031 // Any other failure must still be reported in the contract's error shape.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ToolFailed(logger, tool, ex);
            throw Failure(ErrorCodes.GovernanceUnavailable, true, "Axiom could not complete the request. No governance verdict was produced.");
        }
    }

    public static McpException Failure(string code, bool retryable, string message) =>
        new(JsonSerializer.Serialize(new { error = new { code, retryable, message } }, Json));

    public static bool IsContractError(McpException exception) =>
        exception.Message.StartsWith("{\"error\":", StringComparison.Ordinal);

    private static readonly Action<ILogger, string, Exception?> ToolFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(1, nameof(ToolFailed)), "MCP tool {Tool} failed");
}
