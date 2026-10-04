using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Axiom.Cli;

/// <summary>Outcome of one API call. <see cref="Body"/> is null when there was no JSON answer.</summary>
internal sealed record ApiResponse(int Status, JsonElement? Body, string? TransportError)
{
    public bool IsSuccess => Status is >= 200 and < 300 && Body is not null;

    /// <summary>The contract's <c>error.code</c>, when the server sent one.</summary>
    public string? ErrorCode => Body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("error", out var e) && e.TryGetProperty("code", out var c) ? c.GetString() : null;

    public string? ErrorMessage => Body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() : null;
}

/// <summary>
/// Thin HTTP client for the Axiom API. The bearer token is attached here and nowhere else, only to the
/// configured base URL, and it is never written to output or error messages.
/// </summary>
internal sealed class AxiomClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public AxiomClient(Uri baseUrl, string token, HttpMessageHandler? handler)
    {
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = baseUrl;
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Task<ApiResponse> PostAsync(string path, object body, CancellationToken cancellationToken) =>
        SendAsync(() => _http.PostAsJsonAsync(new Uri(path, UriKind.Relative), body, Json, cancellationToken), cancellationToken);

    public Task<ApiResponse> GetAsync(string path, CancellationToken cancellationToken) =>
        SendAsync(() => _http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken), cancellationToken);

    private static async Task<ApiResponse> SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send();
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonElement? body = null;
            if (text.Length > 0)
            {
                try
                {
                    body = JsonDocument.Parse(text).RootElement.Clone();
                }
                catch (JsonException)
                {
                    // Not JSON (for example a proxy error page): reported by status only.
                }
            }

            return new ApiResponse((int)response.StatusCode, body, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The exception text may quote the URL; only its kind is reported.
            return new ApiResponse(0, null, ex.GetType().Name);
        }
    }

    public void Dispose() => _http.Dispose();
}
