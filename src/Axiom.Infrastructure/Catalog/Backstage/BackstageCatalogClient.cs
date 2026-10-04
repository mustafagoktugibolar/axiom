using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Axiom.Application.Catalog;
using Axiom.Domain.Catalog;
using Axiom.Infrastructure.Catalog.Manifests;
using Microsoft.Extensions.Options;

namespace Axiom.Infrastructure.Catalog.Backstage;

/// <summary>Settings of the optional Backstage adapter, bound from <c>Axiom:Catalog:Backstage</c>.</summary>
public sealed class BackstageCatalogOptions
{
    public const string SectionName = "Axiom:Catalog:Backstage";

    /// <summary>Root of the Backstage backend, e.g. <c>https://backstage.example.com</c>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Bearer token for the catalog API. A secret: supplied through configuration, never logged.</summary>
    public string? Token { get; set; }

    public int PageSize { get; set; } = 500;

    /// <summary>Refuses to import more than this many entities rather than importing a partial catalog.</summary>
    public int MaxEntities { get; set; } = 100_000;

    /// <summary>
    /// The base URL must be absolute https without credentials, query or fragment; plain http is accepted
    /// only for loopback hosts. This keeps configuration from pointing the server at arbitrary internal
    /// plaintext endpoints (SSRF).
    /// </summary>
    public static bool TryValidateBaseUrl(string? value, out Uri? baseUri, out string? error)
    {
        baseUri = null;
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            error = "Backstage BaseUrl must be an absolute URL.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "Backstage BaseUrl must not contain credentials, a query or a fragment.";
            return false;
        }

        var isLoopbackHttp = uri.Scheme == Uri.UriSchemeHttp && IsLoopback(uri);
        if (uri.Scheme != Uri.UriSchemeHttps && !isLoopbackHttp)
        {
            error = "Backstage BaseUrl must use https (http is allowed only for loopback hosts).";
            return false;
        }

        baseUri = new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
        error = null;
        return true;
    }

    private static bool IsLoopback(Uri uri) =>
        string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));
}

/// <summary>
/// Reads Domain, System, Component, API, Resource and Group entities from the Backstage catalog REST
/// API and maps them to graph facts with provenance <see cref="ProvenanceSource.Catalog"/>. Any failed
/// or malformed page fails the whole fetch: a partial catalog must never be imported, because an
/// import retracts what its source no longer reports.
/// </summary>
public sealed class BackstageCatalogClient : IBackstageCatalogClient
{
    private const string KindFilter = "filter=kind=domain&filter=kind=system&filter=kind=component&filter=kind=api&filter=kind=resource&filter=kind=group";

    private readonly HttpClient _http;
    private readonly BackstageCatalogOptions _options;
    private readonly Uri _baseUri;

    public BackstageCatalogClient(HttpClient http, IOptions<BackstageCatalogOptions> options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options.Value;
        if (!BackstageCatalogOptions.TryValidateBaseUrl(_options.BaseUrl, out var baseUri, out var error))
        {
            throw new InvalidOperationException(error);
        }

        _baseUri = baseUri!;
    }

    /// <summary>Source locator under which everything imported from this Backstage instance is recorded.</summary>
    public string SourceLocator => "backstage:" + _baseUri.Authority + _baseUri.AbsolutePath.TrimEnd('/');

    public async Task<CatalogImport> FetchAsync(string organizationId, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        var pageSize = Math.Clamp(_options.PageSize, 1, 1000);
        var builder = new CatalogImportBuilder(ProvenanceSource.Catalog, SourceLocator, observedAt);
        var errors = new List<string>();
        var fetched = 0;

        while (true)
        {
            var page = await GetPageAsync(fetched, pageSize, cancellationToken).ConfigureAwait(false);
            foreach (var item in page)
            {
                if (item is not JsonObject entity)
                {
                    throw new InvalidOperationException("Backstage returned a catalog entry that is not an object.");
                }

                BackstageEntityMapper.Map(entity, builder, errors);
            }

            fetched += page.Count;
            if (page.Count < pageSize)
            {
                break;
            }

            if (fetched >= _options.MaxEntities)
            {
                throw new InvalidOperationException($"Backstage catalog exceeds the configured limit of {_options.MaxEntities} entities; nothing was imported.");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Backstage returned {errors.Count} entity field(s) that could not be mapped; nothing was imported. First: {errors[0]}");
        }

        return builder.Build();
    }

    private async Task<JsonArray> GetPageAsync(int offset, int limit, CancellationToken cancellationToken)
    {
        var uri = new Uri(_baseUri, string.Create(CultureInfo.InvariantCulture, $"api/catalog/entities?{KindFilter}&limit={limit}&offset={offset}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(_options.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Backstage catalog request failed with status {(int)response.StatusCode}.", null, response.StatusCode);
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false) as JsonArray
                    ?? throw new InvalidOperationException("Backstage catalog response is not a JSON array of entities.");
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("Backstage catalog response is not valid JSON.", exception);
            }
        }
    }
}
