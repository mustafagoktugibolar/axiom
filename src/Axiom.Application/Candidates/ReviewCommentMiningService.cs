using System.Collections.Immutable;
using System.Text;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Application.Exceptions;
using Axiom.Domain.Catalog;
using Axiom.Domain.Governance;

namespace Axiom.Application.Candidates;

/// <param name="Themes">Recurring themes found; each became (or already was) a candidate.</param>
public sealed record ReviewMiningResult(
    string Repository,
    int PullRequestsScanned,
    int CommentsRead,
    int MinOccurrences,
    int MinPullRequests,
    int Themes,
    int Created,
    int Existing,
    bool Truncated);

/// <summary>
/// Mines repeated pull-request review comments (task 11.3). Comments are clustered by normalized text; a theme
/// becomes a proposed candidate only when it repeats at least N times across at least M pull requests, because one
/// reviewer's one-off remark is not a team norm. The candidate is a lead for a human: it quotes the most typical
/// comment and lists the pull requests as evidence. It is idempotent: the theme is identified by repository and the
/// words of its earliest comment.
/// </summary>
public sealed class ReviewCommentMiningService(IReviewCommentSource source, CandidateService candidates)
{
    public const int DefaultMinOccurrences = 3;
    public const int DefaultMinPullRequests = 2;
    public const int DefaultMaxPullRequests = 50;
    public const int MaxPullRequestsLimit = 100;
    public const int MaxThemes = 100;
    public const int EvidenceLines = 10;

    public async Task<ReviewMiningResult> MineAsync(
        AxiomPrincipal principal, string repository, int? minOccurrences, int? minPullRequests, int? maxPullRequests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.ManageGovernanceSource);
        var name = RequestLimits.Identifier(repository, "repository");
        var occurrences = minOccurrences ?? DefaultMinOccurrences;
        var pullRequests = minPullRequests ?? DefaultMinPullRequests;
        var window = maxPullRequests ?? DefaultMaxPullRequests;
        if (occurrences is < 2 or > 100)
        {
            throw AxiomException.Invalid("'minOccurrences' must be between 2 and 100.");
        }

        if (pullRequests < 1 || pullRequests > occurrences)
        {
            throw AxiomException.Invalid("'minPullRequests' must be at least 1 and at most 'minOccurrences'.");
        }

        if (window is < 1 or > MaxPullRequestsLimit)
        {
            throw AxiomException.Invalid($"'maxPullRequests' must be between 1 and {MaxPullRequestsLimit}.");
        }

        var batch = await source.GetRecentAsync(new ReviewCommentRequest(principal.OrganizationId, name, window), cancellationToken);
        var themes = ReviewCommentClusterer.FindThemes(batch.Comments, occurrences, pullRequests);
        var inputs = themes.Take(MaxThemes).Select(t => ToInput(name, t, occurrences, pullRequests)).ToList();
        var result = inputs.Count == 0
            ? new CandidateIngestResult(0, 0, [], [])
            : await candidates.IngestAsync(principal, inputs, cancellationToken);
        return new ReviewMiningResult(
            name, batch.PullRequestsScanned, batch.Comments.Length, occurrences, pullRequests, themes.Length,
            result.Created, result.Existing, batch.Truncated || themes.Length > MaxThemes);
    }

    internal static CandidateInput ToInput(string repository, ReviewTheme theme, int minOccurrences, int minPullRequests)
    {
        var statement = CandidateText.Shorten(CandidateText.Collapse(Prose(theme.Representative.Body)), 400);
        var first = theme.Comments.OrderBy(c => c.CreatedAt).ThenBy(c => c.CommentId, StringComparer.Ordinal).First();

        var rationale = new StringBuilder();
        rationale.Append("Reviewers made this point ").Append(theme.Comments.Length).Append(" times across ").Append(theme.PullRequestCount)
            .Append(" pull requests (").Append(theme.AuthorCount).Append(" reviewer").Append(theme.AuthorCount == 1 ? "" : "s")
            .Append("); the threshold was ").Append(minOccurrences).Append(" comments in ").Append(minPullRequests).AppendLine(" pull requests.").AppendLine();
        rationale.AppendLine("Evidence:");
        foreach (var comment in theme.Comments.OrderBy(c => c.CreatedAt).ThenBy(c => c.CommentId, StringComparer.Ordinal).Take(EvidenceLines))
        {
            rationale.Append("- PR ").Append(comment.PullRequestId).Append(", comment ").Append(comment.CommentId);
            if (!string.IsNullOrEmpty(comment.FilePath))
            {
                rationale.Append(" on `").Append(comment.FilePath).Append('`');
            }

            if (comment.Url is { } url && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                rationale.Append(" (").Append(url).Append(')');
            }

            rationale.AppendLine();
        }

        if (theme.Comments.Length > EvidenceLines)
        {
            rationale.Append("- and ").Append(theme.Comments.Length - EvidenceLines).AppendLine(" more.");
        }

        var confidence = 0.3 + (0.05 * (theme.Comments.Length - minOccurrences)) + (0.05 * (theme.PullRequestCount - minPullRequests)) + (theme.AuthorCount >= 3 ? 0.1 : 0);
        var shortName = EntityRef.TryParse(repository, out var reference) ? reference.Name : repository.Trim();
        var locator = CandidateText.Shorten($"{repository}:review-comments#pr{first.PullRequestId}/c{first.CommentId}+{theme.Comments.Length - 1}", 250);
        return new CandidateInput(
            locator,
            CandidateText.Hash($"review-theme\n{repository}\n{string.Join(' ', theme.SeedTokens)}"),
            ReviewCommentClusterer.Method,
            Math.Min(0.7, confidence),
            null,
            RecordKind.Standard,
            CandidateText.Shorten("Recurring review feedback: " + statement, 120),
            statement,
            rationale.ToString().TrimEnd(),
            new Dictionary<string, string[]> { ["repositories"] = [shortName] });
    }

    /// <summary>Review text without code blocks and quoted replies, so the statement reads as the reviewer's own words.</summary>
    private static string Prose(string body)
    {
        var lines = new List<string>();
        var inFence = false;
        foreach (var line in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }

            if (!inFence && !line.TrimStart().StartsWith('>'))
            {
                lines.Add(line);
            }
        }

        return string.Join(' ', lines);
    }
}
