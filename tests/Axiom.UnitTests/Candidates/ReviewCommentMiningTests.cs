using System.Collections.Immutable;
using Axiom.Application.Candidates;
using Axiom.Application.Common;
using Axiom.Application.Exceptions;
using Axiom.Domain.Governance;
using Microsoft.Extensions.Time.Testing;

namespace Axiom.UnitTests.Candidates;

public class ReviewCommentMiningTests
{
    private const string Org = "acme";
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly InMemoryCandidateStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly AxiomPrincipal _admin = new("user:admin", "Admin", Org, [Role.PlatformAdmin], []);

    private static ReviewComment Comment(string pr, string id, string body, string author = "alice", int day = 0) =>
        new(pr, id, author, body, "src/Payments.cs", $"https://github.com/acme/gateway/pull/{pr}#discussion_r{id}", Start.AddDays(day));

    private ReviewCommentMiningService Miner(params ReviewComment[] comments) =>
        new(new FakeReviewSource(comments), new CandidateService(_store, _time));

    private static ReviewComment[] TimeoutTheme() =>
    [
        Comment("1", "10", "Please add a timeout to this outbound HTTP call.", "alice", 0),
        Comment("2", "11", "Add a timeout to the outbound HTTP calls here.", "bob", 1),
        Comment("3", "12", "This outbound http call needs a timeout. See `HttpClient.Timeout`.", "carol", 2),
        Comment("3", "13", "LGTM", "alice", 2),
        Comment("4", "14", "Rename this variable to something clearer.", "bob", 3),
    ];

    [Fact]
    public void Similar_comments_are_clustered_by_normalized_text()
    {
        var themes = ReviewCommentClusterer.FindThemes(TimeoutTheme(), 3, 2);

        var theme = Assert.Single(themes);
        Assert.Equal(3, theme.Comments.Length);
        Assert.Equal(3, theme.PullRequestCount);
        Assert.Equal(3, theme.AuthorCount);
    }

    [Fact]
    public void Normalization_drops_code_links_mentions_and_filler()
    {
        var tokens = ReviewCommentClusterer.Tokens("@bob Please add `foo()` timeouts, see https://example.com/x\n> quoted\n```\nignored()\n```");

        Assert.Equal(["add", "see", "timeout"], tokens.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_theme_needs_both_enough_comments_and_enough_pull_requests()
    {
        var sameThread = new[]
        {
            Comment("1", "1", "Please add a timeout to this outbound HTTP call."),
            Comment("1", "2", "Add a timeout to this outbound HTTP call too."),
            Comment("1", "3", "Another outbound HTTP call that needs a timeout."),
        };

        Assert.Empty(ReviewCommentClusterer.FindThemes(sameThread, 3, 2));
        Assert.Single(ReviewCommentClusterer.FindThemes(sameThread, 3, 1));
        Assert.Empty(ReviewCommentClusterer.FindThemes(TimeoutTheme(), 4, 2));
    }

    [Fact]
    public void Bot_comments_do_not_count()
    {
        var comments = TimeoutTheme().Select(c => c with { Author = "lint[bot]" });

        Assert.Empty(ReviewCommentClusterer.FindThemes(comments, 3, 2));
    }

    [Fact]
    public async Task A_repeated_theme_becomes_one_proposed_candidate_with_evidence()
    {
        var result = await Miner(TimeoutTheme()).MineAsync(_admin, "gateway", null, null, null, default);

        Assert.Equal((1, 1, 0, 5), (result.Themes, result.Created, result.Existing, result.CommentsRead));
        var candidate = Assert.Single(_store.Items);
        Assert.Equal(CandidateState.Proposed, candidate.State);
        Assert.False(candidate.IsAuthoritative);
        Assert.Equal(ReviewCommentClusterer.Method, candidate.Evidence.ExtractionMethod);
        Assert.InRange(candidate.Evidence.Confidence, 0.3, 0.7);
        Assert.Contains("3 times across 3 pull requests", candidate.Draft.Rationale, StringComparison.Ordinal);
        Assert.Contains("https://github.com/acme/gateway/pull/2#discussion_r11", candidate.Draft.Rationale, StringComparison.Ordinal);
        Assert.Contains("timeout", candidate.Draft.Statement, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("gateway:review-comments#pr1/c10", candidate.Evidence.SourceLocator, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_off_remarks_make_no_candidate()
    {
        var result = await Miner(TimeoutTheme().Take(2).ToArray()).MineAsync(_admin, "gateway", null, null, null, default);

        Assert.Equal(0, result.Themes);
        Assert.Empty(_store.Items);
    }

    [Fact]
    public async Task Mining_again_is_idempotent_even_when_the_theme_grows()
    {
        await Miner(TimeoutTheme()).MineAsync(_admin, "gateway", null, null, null, default);
        var grown = TimeoutTheme().Append(Comment("5", "20", "Please add a timeout to this outbound HTTP call.", "dave", 5)).ToArray();

        var again = await Miner(grown).MineAsync(_admin, "gateway", null, null, null, default);

        Assert.Equal((0, 1), (again.Created, again.Existing));
        Assert.Single(_store.Items);
    }

    [Fact]
    public async Task Thresholds_are_configurable_and_validated()
    {
        var strict = await Miner(TimeoutTheme()).MineAsync(_admin, "gateway", 4, 2, null, default);
        Assert.Equal(0, strict.Themes);
        Assert.Equal((4, 2), (strict.MinOccurrences, strict.MinPullRequests));

        var miner = Miner(TimeoutTheme());
        await Assert.ThrowsAsync<AxiomException>(() => miner.MineAsync(_admin, "gateway", 1, null, null, default));
        await Assert.ThrowsAsync<AxiomException>(() => miner.MineAsync(_admin, "gateway", 3, 4, null, default));
        await Assert.ThrowsAsync<AxiomException>(() => miner.MineAsync(_admin, "gateway", null, null, 0, default));
    }

    [Fact]
    public async Task Mining_needs_governance_source_rights()
    {
        var reader = new AxiomPrincipal("user:dev", "Dev", Org, [Role.Reader], []);

        await Assert.ThrowsAsync<AxiomException>(() => Miner(TimeoutTheme()).MineAsync(reader, "gateway", null, null, null, default));
    }

    private sealed class FakeReviewSource(ReviewComment[] comments) : IReviewCommentSource
    {
        public Task<ReviewCommentBatch> GetRecentAsync(ReviewCommentRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new ReviewCommentBatch([.. comments], comments.Select(c => c.PullRequestId).Distinct().Count(), false));
    }
}
