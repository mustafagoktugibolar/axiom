using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Axiom.Application.Semantic;
using Microsoft.Extensions.Options;

namespace Axiom.Infrastructure.Semantic;

/// <summary>Settings of the hosted model provider, bound from <c>Axiom:Semantic:Anthropic</c>.</summary>
public sealed class AnthropicOptions
{
    public const string SectionName = "Axiom:Semantic:Anthropic";

    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-haiku-5-5";

    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    public int MaxTokens { get; set; } = 1_024;
}

/// <summary>
/// Calls the Anthropic Messages API and forces the answer through one tool whose input schema is the
/// typed contract (task 7.3), so the model cannot answer in free text. The API key leaves the process
/// only towards the configured base URL, and redirects are never followed.
/// </summary>
public sealed class AnthropicSemanticProvider(IHttpClientFactory httpClients, IOptions<AnthropicOptions> options) : ISemanticProvider
{
    public const string HttpClientName = "axiom-semantic-anthropic";
    private const string ApiVersion = "2023-06-01";

    private readonly AnthropicOptions _options = options.Value;

    public string Name => "anthropic";

    public string Model => _options.Model;

    public bool IsExternal => true;

    public async Task<SemanticProviderOutput> AnalyzeAsync(EvidencePacket packet, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var prompt = SemanticPromptBuilder.Build(packet);
        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["max_tokens"] = _options.MaxTokens,
            ["system"] = prompt.System,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt.User }),
            ["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = SemanticPromptBuilder.ToolName,
                ["description"] = "Report the governance findings, or an empty list when there are none.",
                ["input_schema"] = SemanticPromptBuilder.OutputSchema.DeepClone(),
            }),
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = SemanticPromptBuilder.ToolName },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.BaseUrl), "/v1/messages")) { Content = JsonContent.Create(body) };
        request.Headers.Add("x-api-key", _options.ApiKey);
        request.Headers.Add("anthropic-version", ApiVersion);

        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

        var root = document.RootElement;
        var tool = root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray().FirstOrDefault(c => c.TryGetProperty("type", out var t) && t.GetString() == "tool_use"
                && c.TryGetProperty("name", out var n) && n.GetString() == SemanticPromptBuilder.ToolName)
            : default;
        if (tool.ValueKind != JsonValueKind.Object || !tool.TryGetProperty("input", out var input))
        {
            throw new SemanticOutputException("The model did not answer through the findings tool.");
        }

        var usage = root.TryGetProperty("usage", out var u)
            ? new SemanticUsage(u.TryGetProperty("input_tokens", out var i) ? i.GetInt32() : 0, u.TryGetProperty("output_tokens", out var o) ? o.GetInt32() : 0)
            : new SemanticUsage(0, 0);
        return new SemanticProviderOutput(SemanticPromptBuilder.ParseFindings(input), usage);
    }
}
