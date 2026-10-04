using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Axiom.Mcp;

public static class McpModule
{
    /// <summary>Serialization of tool inputs and outputs: camelCase, enums as strings, no nulls. Matches the REST surface.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = Create();

    /// <summary>Adds the governance tools to an MCP server. Hosts choose the transport and authentication.</summary>
    public static IMcpServerBuilder AddAxiomTools(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .WithTools<GovernanceTools>(JsonOptions)
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                try
                {
                    return await next(context, cancellationToken);
                }
                catch (McpException ex) when (McpErrors.IsContractError(ex))
                {
                    // The SDK would wrap the message in prose; the contract's body is the bare JSON object.
                    return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = ex.Message }] };
                }
            }));
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
