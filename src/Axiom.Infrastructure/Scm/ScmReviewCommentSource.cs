using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Axiom.Application.Candidates;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Domain.Catalog;
using Microsoft.Extensions.Options;

namespace Axiom.Infrastructure.Scm;

/// <summary>
/// Reads pull-request review comments from GitHub and Azure DevOps (task 11.3). The repository's provider comes from
/// the same <see cref="ScmOptions.Providers"/> as status publishing, keyed by the host of its clone URL, and follows
/// the same token rules: https only, the token goes only to that provider's API base, and redirects are never followed
/// (the named client is configured that way). Read-only GET requests; there is no write path.
/// </summary>
public sealed class ScmReviewCommentSource(ISystemGraph graph, IOptionsMonitor<ScmOptions> options, IHttpClientFactory httpClients) : IReviewCommentSource
{
    public const string HttpClientName = "axiom-scm-reviews";
    public const int PageSize = 100;
    public const int MaxPages = 5;
    public const int MaxCommentLength = 4_000;

    public async Task<ReviewCommentBatch> GetRecentAsync(ReviewCommentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = options.CurrentValue;
        var entity = await graph.ResolveRepositoryAsync(request.OrganizationId, request.Repository, cancellationToken);
        var node = entity is null ? null : await graph.FindAsync(request.OrganizationId, entity.Value, cancellationToken);
        if (node is null || !node.Attributes.TryGetValue(RepositoryAttributes.CloneUrls, out var urls))
        {
            throw Unavailable($"Repository '{request.Repository}' declares no clone URL in the System Graph, so its review comments cannot be read.");
        }

        foreach (var url in RepositoryAttributes.SplitList(urls))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var clone) || clone.Scheme != Uri.UriSchemeHttps
                || !settings.Providers.TryGetValue(clone.Host, out var provider) || string.IsNullOrEmpty(provider.Token))
            {
                continue;
            }

            switch (provider.Type)
            {
                case ScmProviderType.GitHub when GitHubBase(clone, provider) is { } target:
                    return await ReadGitHubAsync(target.Api, target.Owner, target.Repository, provider, request.MaxPullRequests, cancellationToken);
                case ScmProviderType.AzureDevOps when AzureDevOpsBase(clone) is { } repo:
                    return await ReadAzureDevOpsAsync(repo, provider, request.MaxPullRequests, cancellationToken);
                default:
                    break;
            }
        }

        throw Unavailable($"No clone URL of repository '{request.Repository}' points to a host with a configured source-control provider token.");
    }

    internal static (Uri Api, string Owner, string Repository)? GitHubBase(Uri clone, ScmProviderOptions provider)
    {
        var segments = clone.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !ScmStatusPublisher.TryApiBase(provider.ApiBaseUrl, clone.Host, out var api))
        {
            return null;
        }

        var repository = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        return (api, segments[0], repository);
    }

    /// <summary>The repositories API root, <c>https://host/{org}/{project}/_apis/git/repositories/{repo}</c>.</summary>
    internal static Uri? AzureDevOpsBase(Uri clone)
    {
        var path = clone.AbsolutePath;
        var marker = path.IndexOf("/_git/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return null;
        }

        var repository = path[(marker + "/_git/".Length)..].Trim('/');
        return repository.Length == 0 || repository.Contains('/', StringComparison.Ordinal)
            ? null
            : new Uri($"{clone.Scheme}://{clone.Authority}{path[..marker]}/_apis/git/repositories/{Uri.EscapeDataString(repository)}/");
    }

    private async Task<ReviewCommentBatch> ReadGitHubAsync(Uri api, string owner, string repository, ScmProviderOptions provider, int maxPullRequests, CancellationToken cancellationToken)
    {
        var comments = ImmutableArray.CreateBuilder<ReviewComment>();
        var pullRequests = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;
        for (var page = 1; page <= MaxPages; page++)
        {
            var uri = new Uri(api, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/pulls/comments?sort=created&direction=desc&per_page={PageSize}&page={page}");
            using var message = new HttpRequestMessage(HttpMethod.Get, uri);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.Token);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            message.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var json = await SendAsync(message, "GitHub", cancellationToken);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var count = 0;
            foreach (var item in json.RootElement.EnumerateArray())
            {
                count++;
                var pr = LastSegment(Str(item, "pull_request_url"));
                if (pr.Length == 0 || Str(item, "body") is not { Length: > 0 } body)
                {
                    continue;
                }

                if (!pullRequests.Contains(pr) && pullRequests.Count >= maxPullRequests)
                {
                    truncated = true;
                    continue;
                }

                pullRequests.Add(pr);
                comments.Add(new ReviewComment(
                    pr, Id(item), item.TryGetProperty("user", out var user) ? Str(user, "login") ?? string.Empty : string.Empty, Clip(body),
                    Str(item, "path"), Str(item, "html_url"), Date(Str(item, "created_at"))));
            }

            if (count < PageSize || truncated)
            {
                break;
            }

            truncated = page == MaxPages;
        }

        return new ReviewCommentBatch(comments.ToImmutable(), pullRequests.Count, truncated);
    }

    private async Task<ReviewCommentBatch> ReadAzureDevOpsAsync(Uri repositoryApi, ScmProviderOptions provider, int maxPullRequests, CancellationToken cancellationToken)
    {
        var list = new Uri(repositoryApi, $"pullrequests?searchCriteria.status=all&$top={maxPullRequests.ToString(CultureInfo.InvariantCulture)}&api-version=7.1");
        using var listJson = await SendAsync(AzureDevOpsRequest(list, provider), "Azure DevOps", cancellationToken);
        var ids = new List<string>();
        if (listJson.RootElement.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            ids.AddRange(values.EnumerateArray().Select(v => Id(v, "pullRequestId")).Where(i => i.Length > 0 && i.All(char.IsAsciiDigit)));
        }

        var comments = ImmutableArray.CreateBuilder<ReviewComment>();
        foreach (var id in ids.Take(maxPullRequests))
        {
            var threadsUri = new Uri(repositoryApi, $"pullrequests/{id}/threads?api-version=7.1");
            using var threads = await SendAsync(AzureDevOpsRequest(threadsUri, provider), "Azure DevOps", cancellationToken);
            if (!threads.RootElement.TryGetProperty("value", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var thread in items.EnumerateArray())
            {
                var path = thread.TryGetProperty("threadContext", out var context) && context.ValueKind == JsonValueKind.Object ? Str(context, "filePath") : null;
                if (!thread.TryGetProperty("comments", out var threadComments) || threadComments.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var comment in threadComments.EnumerateArray())
                {
                    if (!string.Equals(Str(comment, "commentType"), "text", StringComparison.OrdinalIgnoreCase) || Str(comment, "content") is not { Length: > 0 } content)
                    {
                        continue;
                    }

                    comments.Add(new ReviewComment(
                        id, $"{Id(thread, "id")}-{Id(comment, "id")}", comment.TryGetProperty("author", out var author) ? Str(author, "displayName") ?? string.Empty : string.Empty,
                        Clip(content), path, null, Date(Str(comment, "publishedDate"))));
                }
            }
        }

        return new ReviewCommentBatch(comments.ToImmutable(), ids.Count, ids.Count >= maxPullRequests);
    }

    private static HttpRequestMessage AzureDevOpsRequest(Uri uri, ScmProviderOptions provider)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, uri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + provider.Token)));
        return message;
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage message, string provider, CancellationToken cancellationToken)
    {
        using (message)
        {
            using var client = httpClients.CreateClient(HttpClientName);
            using var response = await client.SendAsync(message, cancellationToken);

            // 3xx is a failure, not a hop: following it could carry the token to another host.
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"The {provider} review-comment API answered {(int)response.StatusCode}.", null, response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
    }

    private static AxiomException Unavailable(string message) => new(ErrorKind.Unavailable, ErrorCodes.ScmUnavailable, message);

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Id(JsonElement element, string name = "id") =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.Number ? value.GetInt64().ToString(CultureInfo.InvariantCulture) : value.GetString() ?? string.Empty
            : string.Empty;

    private static string LastSegment(string? url) => url is null ? string.Empty : url[(url.LastIndexOf('/') + 1)..];

    private static DateTimeOffset Date(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : DateTimeOffset.UnixEpoch;

    private static string Clip(string body) => body.Length <= MaxCommentLength ? body : body[..MaxCommentLength];
}
