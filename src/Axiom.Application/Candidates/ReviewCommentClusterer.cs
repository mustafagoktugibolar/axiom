using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Axiom.Application.Candidates;

/// <summary>Comments that say the same thing, with the evidence that they repeat.</summary>
public sealed record ReviewTheme(
    ImmutableArray<ReviewComment> Comments,
    ReviewComment Representative,
    ImmutableArray<string> SeedTokens,
    int PullRequestCount,
    int AuthorCount);

/// <summary>
/// Groups review comments that are about the same thing by their normalized text. Normalization lower-cases, drops
/// code, links, mentions and quotes, removes common words and folds simple plurals and verb endings; two comments
/// are the same theme when their word sets overlap by at least <see cref="SimilarityThreshold"/> (Jaccard). The
/// algorithm is a single ordered pass over the comments, so the same input always gives the same themes.
/// </summary>
public static partial class ReviewCommentClusterer
{
    public const string Method = "review-comments/jaccard-cluster@1";
    public const double SimilarityThreshold = 0.5;
    public const int MinTokens = 3;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "this", "that", "these", "those", "it", "its", "is", "are", "was", "were", "be", "been", "to", "of", "in", "on", "for", "and", "or",
        "but", "with", "as", "at", "by", "we", "you", "i", "me", "my", "our", "your", "can", "could", "would", "should", "will", "do", "does", "did", "please",
        "here", "there", "so", "if", "then", "just", "also", "not", "no", "maybe", "perhaps", "pls", "thanks", "thank", "nit", "lgtm",
    };

    /// <summary>Clusters the comments and returns the themes that repeat at least <paramref name="minOccurrences"/> times across at least <paramref name="minPullRequests"/> pull requests.</summary>
    public static ImmutableArray<ReviewTheme> FindThemes(IEnumerable<ReviewComment> comments, int minOccurrences, int minPullRequests)
    {
        ArgumentNullException.ThrowIfNull(comments);
        var clusters = new List<Cluster>();
        var ordered = comments
            .Where(c => !c.Author.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.PullRequestId, StringComparer.Ordinal)
            .ThenBy(c => c.CommentId, StringComparer.Ordinal);
        foreach (var comment in ordered)
        {
            var tokens = Tokens(comment.Body);
            if (tokens.Count < MinTokens)
            {
                continue;
            }

            var home = clusters.FirstOrDefault(c => Jaccard(c.Seed, tokens) >= SimilarityThreshold);
            if (home is null)
            {
                clusters.Add(home = new Cluster(tokens));
            }

            home.Members.Add((comment, tokens));
        }

        var themes = ImmutableArray.CreateBuilder<ReviewTheme>();
        foreach (var cluster in clusters)
        {
            var pullRequests = cluster.Members.Select(m => m.Comment.PullRequestId).Distinct(StringComparer.Ordinal).Count();
            if (cluster.Members.Count < minOccurrences || pullRequests < minPullRequests)
            {
                continue;
            }

            var representative = cluster.Members
                .Select(m => (m.Comment, Score: cluster.Members.Sum(o => Jaccard(m.Tokens, o.Tokens))))
                .OrderByDescending(x => x.Score).ThenBy(x => x.Comment.CreatedAt).ThenBy(x => x.Comment.CommentId, StringComparer.Ordinal)
                .First().Comment;
            themes.Add(new ReviewTheme(
                [.. cluster.Members.Select(m => m.Comment)],
                representative,
                [.. cluster.Seed.Order(StringComparer.Ordinal)],
                pullRequests,
                cluster.Members.Select(m => m.Comment.Author).Distinct(StringComparer.OrdinalIgnoreCase).Count()));
        }

        return [.. themes.OrderByDescending(t => t.Comments.Length).ThenBy(t => string.Join(' ', t.SeedTokens), StringComparer.Ordinal)];
    }

    /// <summary>The normalized word set of a comment. Public so the normalization can be tested and explained.</summary>
    public static HashSet<string> Tokens(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var text = CodeFence().Replace(body, " ");
        text = QuoteLine().Replace(text, " ");
        text = InlineCode().Replace(text, " ");
        text = Url().Replace(text, " ");
        text = Mention().Replace(text, " ");
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Word().Matches(text.ToLowerInvariant()))
        {
            var word = match.Value;
            if (word.Length < 2 || StopWords.Contains(word))
            {
                continue;
            }

            word = Stem(word);
            if (!StopWords.Contains(word))
            {
                tokens.Add(word);
            }
        }

        return tokens;
    }

    internal static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var shared = a.Count(b.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    private static string Stem(string word)
    {
        foreach (var suffix in new[] { "ing", "ed", "es", "s" })
        {
            if (word.Length > suffix.Length + 3 && word.EndsWith(suffix, StringComparison.Ordinal))
            {
                return word[..^suffix.Length];
            }
        }

        return word;
    }

    private sealed class Cluster(HashSet<string> seed)
    {
        public HashSet<string> Seed { get; } = seed;

        public List<(ReviewComment Comment, HashSet<string> Tokens)> Members { get; } = [];
    }

    [GeneratedRegex(@"```.*?```", RegexOptions.Singleline)]
    private static partial Regex CodeFence();

    [GeneratedRegex(@"^\s*>.*$", RegexOptions.Multiline)]
    private static partial Regex QuoteLine();

    [GeneratedRegex(@"`[^`]*`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex Url();

    [GeneratedRegex(@"@[\w.\-]+")]
    private static partial Regex Mention();

    [GeneratedRegex(@"[\p{L}][\p{L}\-]*")]
    private static partial Regex Word();
}
