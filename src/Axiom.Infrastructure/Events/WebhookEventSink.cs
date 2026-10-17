using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Axiom.Application.Common;

namespace Axiom.Infrastructure.Events;

/// <summary>
/// POSTs the event envelope to one https endpoint, signed so the receiver can verify origin and freshness.
/// The signature is <c>sha256=hex(HMAC-SHA256(secret, "{timestamp}.{body}"))</c> in <c>X-Axiom-Signature</c>, with the
/// Unix timestamp in <c>X-Axiom-Timestamp</c>; <c>X-Axiom-Event-Id</c> is stable across retries for idempotency.
/// Redirects are not followed, so a signed body is never replayed to another host.
/// </summary>
public sealed class WebhookEventSink : IEventSink
{
    public const string HttpClientName = "axiom-events-webhook";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Uri _url;
    private readonly byte[] _secret;
    private readonly IHttpClientFactory _httpClients;
    private readonly TimeProvider _time;

    public WebhookEventSink(WebhookEndpointOptions endpoint, IHttpClientFactory httpClients, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"Webhook '{endpoint.Name}' must have an absolute https URL.");
        }

        if (string.IsNullOrEmpty(endpoint.Secret))
        {
            throw new InvalidOperationException($"Webhook '{endpoint.Name}' has no signing secret.");
        }

        Name = "webhook:" + endpoint.Name;
        _url = url;
        _secret = Encoding.UTF8.GetBytes(endpoint.Secret);
        _httpClients = httpClients;
        _time = time;
    }

    public string Name { get; }

    public async Task DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = Serialize(message);
        var timestamp = _time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        using var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Axiom-Event-Id", message.EventId);
        request.Headers.Add("X-Axiom-Event-Type", message.EventType);
        request.Headers.Add("X-Axiom-Timestamp", timestamp);
        request.Headers.Add("X-Axiom-Signature", Sign(_secret, timestamp, body));
        if (message.Traceparent is { Length: > 0 } traceparent)
        {
            request.Headers.TryAddWithoutValidation("traceparent", traceparent);
        }

        using var response = await _httpClients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Status only: the response body is the receiver's and may echo the payload.
            throw new HttpRequestException($"Webhook '{Name}' answered {(int)response.StatusCode}.");
        }
    }

    public static string Sign(byte[] secret, string timestamp, string body) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"{timestamp}.{body}")));

    private static string Serialize(OutboxMessage message)
    {
        var envelope = new JsonObject
        {
            ["eventId"] = message.EventId,
            ["eventType"] = message.EventType,
            ["schemaVersion"] = message.SchemaVersion,
            ["organizationId"] = message.OrganizationId,
            ["occurredAt"] = message.OccurredAt,
            ["data"] = JsonNode.Parse(message.DataJson),
        };
        return envelope.ToJsonString(JsonOptions);
    }
}
