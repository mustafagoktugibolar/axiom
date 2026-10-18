using System.Collections.Immutable;

namespace Axiom.Application.Candidates;

/// <summary>One review comment on a pull request, as the source-control system reports it.</summary>
public sealed record ReviewComment(
    string PullRequestId,
    string CommentId,
    string Author,
    string Body,
    string? FilePath,
    string? Url,
    DateTimeOffset CreatedAt);

/// <param name="MaxPullRequests">Upper bound on how many recent pull requests the adapter reads.</param>
public sealed record ReviewCommentRequest(string OrganizationId, string Repository, int MaxPullRequests);

/// <param name="Truncated">True when the adapter stopped at its page or pull-request limit.</param>
public sealed record ReviewCommentBatch(ImmutableArray<ReviewComment> Comments, int PullRequestsScanned, bool Truncated);

/// <summary>
/// Reads recent pull-request review comments of a registered repository (task 11.3). Adapters use the SCM provider
/// configuration and the same token rules as status publishing: a token goes only to its own API host over https,
/// and redirects are never followed. It only reads.
/// </summary>
public interface IReviewCommentSource
{
    Task<ReviewCommentBatch> GetRecentAsync(ReviewCommentRequest request, CancellationToken cancellationToken);
}
