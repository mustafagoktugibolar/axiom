using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Axiom.Application.Catalog;
using Axiom.Application.Evaluation;
using Axiom.Domain.Catalog;
using Axiom.Domain.Evaluation;
using Microsoft.Extensions.Options;

namespace Axiom.Infrastructure.Scm;

/// <summary>
/// Publishes verdicts as GitHub check runs (8.10) and Azure DevOps pull-request or commit statuses (8.9).
/// The target is derived from the repository's clone URL in the System Graph and must have a configured
/// provider; tokens leave the process only towards that provider's API.
/// </summary>
public sealed class ScmStatusPublisher(ISystemGraph graph, IOptionsMonitor<ScmOptions> options, IHttpClientFactory httpClients) : IScmStatusPublisher
{
    public const string HttpClientName = "axiom-scm-status";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<bool> PublishAsync(ScmStatusReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var settings = options.CurrentValue;
        if (!settings.PublishStatus)
        {
            return false;
        }

        var entity = await graph.ResolveRepositoryAsync(report.OrganizationId, report.Repository, cancellationToken);
        var node = entity is null ? null : await graph.FindAsync(report.OrganizationId, entity.Value, cancellationToken);
        if (node is null || !node.Attributes.TryGetValue(RepositoryAttributes.CloneUrls, out var urls))
        {
            return false;
        }

        foreach (var url in RepositoryAttributes.SplitList(urls))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var clone) || clone.Scheme != Uri.UriSchemeHttps
                || !settings.Providers.TryGetValue(clone.Host, out var provider) || string.IsNullOrEmpty(provider.Token))
            {
                continue;
            }

            var request = provider.Type switch
            {
                ScmProviderType.GitHub => BuildGitHub(clone, provider, report),
                ScmProviderType.AzureDevOps => BuildAzureDevOps(clone, provider, report),
                _ => null,
            };
            if (request is null)
            {
                continue;
            }

            using (request)
            {
                using var client = httpClients.CreateClient(HttpClientName);
                using var response = await client.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"The {provider.Type} status API answered {(int)response.StatusCode}.", null, response.StatusCode);
                }
            }

            return true;
        }

        return false;
    }

    internal static HttpRequestMessage? BuildGitHub(Uri clone, ScmProviderOptions provider, ScmStatusReport report)
    {
        var segments = clone.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !TryApiBase(provider.ApiBaseUrl, clone.Host, out var api))
        {
            return null;
        }

        var owner = segments[0];
        var repository = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        var body = new
        {
            name = provider.CheckName,
            head_sha = report.CommitSha,
            status = "completed",
            conclusion = report.Verdict switch
            {
                Verdict.Allow or Verdict.AllowWithWarnings => "success",
                Verdict.RequireReview => "action_required",
                _ => "failure",
            },
            details_url = report.DetailsUrl,
            external_id = report.EvaluationId,
            output = new { title = Truncate(report.Title, 250), summary = Truncate(report.Summary, 60_000) },
        };

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(api, $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/check-runs"))
        {
            Content = JsonContent.Create(body, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    internal static HttpRequestMessage? BuildAzureDevOps(Uri clone, ScmProviderOptions provider, ScmStatusReport report)
    {
        // https://dev.azure.com/{org}/{project}/_git/{repo}  or  https://{host}/{collection}/{project}/_git/{repo}
        var path = clone.AbsolutePath;
        var marker = path.IndexOf("/_git/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return null;
        }

        var projectPath = path[..marker];
        var repository = path[(marker + "/_git/".Length)..].Trim('/');
        if (repository.Length == 0 || repository.Contains('/', StringComparison.Ordinal))
        {
            return null;
        }

        var target = report.PullRequest is { Length: > 0 } pr && pr.All(char.IsAsciiDigit)
            ? $"pullRequests/{pr}/statuses"
            : $"commits/{Uri.EscapeDataString(report.CommitSha)}/statuses";
        var uri = new Uri($"{clone.Scheme}://{clone.Authority}{projectPath}/_apis/git/repositories/{Uri.EscapeDataString(repository)}/{target}?api-version=7.1");
        var body = new
        {
            state = report.Verdict switch
            {
                Verdict.Allow or Verdict.AllowWithWarnings => "succeeded",
                Verdict.RequireReview => "pending",
                _ => "failed",
            },
            description = Truncate(report.Title, 390),
            targetUrl = report.DetailsUrl,
            context = new { name = "governance", genre = provider.CheckName },
        };

        var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body, options: Json) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + provider.Token)));
        return request;
    }

    private static bool TryApiBase(string? configured, string host, out Uri api)
    {
        var text = string.IsNullOrWhiteSpace(configured) ? "https://api.github.com" : configured.Trim();
        if (!text.EndsWith('/'))
        {
            text += "/";
        }

        // The token goes only to https, and github.com must use the public API host.
        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(parsed.UserInfo)
            || (host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && !parsed.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)))
        {
            api = null!;
            return false;
        }

        api = parsed;
        return true;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
