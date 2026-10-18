using System.Collections.Immutable;
using System.Text;
using Axiom.Application.Common;
using Axiom.Domain.Governance;
using Microsoft.Extensions.Options;

namespace Axiom.Application.Candidates;

/// <summary>Bound from <c>Axiom:Candidates:Clustering</c>.</summary>
public sealed class CandidateClusteringOptions
{
    public const string SectionName = "Axiom:Candidates:Clustering";

    /// <summary>Token-set Jaccard similarity at or above which two candidates of the same kind are the same knowledge.</summary>
    public double Threshold { get; set; } = 0.6;

    /// <summary>Upper bound of candidates compared (the comparison is quadratic). Anything beyond is reported as truncated.</summary>
    public int MaxCandidates { get; set; } = 2_000;
}

public sealed record CandidateClusterMember(
    string Id, string State, string SourceLocator, string ExtractionMethod, double Confidence, string? SuggestedOwner, double SimilarityToRepresentative);

/// <param name="Id">Derived from the smallest member id, so it stays the same as long as that candidate is a member.</param>
public sealed record CandidateCluster(
    string Id, string Kind, int Size, CandidateDto Representative, ImmutableArray<CandidateClusterMember> Members,
    ImmutableArray<string> SuggestedOwners, ImmutableArray<string> ExtractionMethods);

public sealed record CandidateClusterResult(
    ImmutableArray<CandidateCluster> Clusters, int CandidatesCompared, int CandidatesTotal, bool Truncated, double Threshold);

/// <summary>
/// Task 11.7: groups duplicate and near-duplicate candidates (the same decision found in an ADR, a doc and PR
/// comments). A read model: nothing is stored, merged or changed, and the candidates keep their own lifecycle.
/// Deterministic normalization plus token-set Jaccard similarity; no embeddings, no external calls. Clusters are the
/// connected components of "similar enough" (single linkage), so the result does not depend on input order.
/// </summary>
public sealed class CandidateClusteringService(ICandidateStore store, IOptions<CandidateClusteringOptions> options)
{
    private const int PageSize = CandidateService.MaxPageSize;

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it", "of", "on", "or", "that", "the", "this", "to", "we", "with", "will", "all", "should", "must", "use", "using",
    };

    private readonly CandidateClusteringOptions _options = options.Value;

    public async Task<CandidateClusterResult> ClusterAsync(AxiomPrincipal principal, bool includeSingletons, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.ReadGovernance);

        var all = new List<Candidate>();
        var total = 0;
        var cap = Math.Max(1, _options.MaxCandidates);
        for (var skip = 0; skip < cap; skip += PageSize)
        {
            var (items, count) = await store.ListAsync(principal.OrganizationId, new CandidateListQuery(null, null, null, skip, Math.Min(PageSize, cap - skip)), cancellationToken);
            total = count;
            all.AddRange(items);
            if (items.Length == 0 || all.Count >= count)
            {
                break;
            }
        }

        var clusters = Cluster(all, _options.Threshold, includeSingletons);
        return new CandidateClusterResult(clusters, all.Count, total, total > all.Count, _options.Threshold);
    }

    /// <summary>Pure clustering, exposed for testing. The result depends only on the set of candidates, not on their order.</summary>
    public static ImmutableArray<CandidateCluster> Cluster(IReadOnlyCollection<Candidate> candidates, double threshold, bool includeSingletons = false)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var ordered = candidates.OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
        var tokens = ordered.Select(c => Tokens(c)).ToArray();
        var parent = Enumerable.Range(0, ordered.Length).ToArray();

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        for (var i = 0; i < ordered.Length; i++)
        {
            for (var j = i + 1; j < ordered.Length; j++)
            {
                if (ordered[i].Draft.Kind == ordered[j].Draft.Kind && Jaccard(tokens[i], tokens[j]) >= threshold)
                {
                    var (a, b) = (Find(i), Find(j));
                    if (a != b)
                    {
                        parent[Math.Max(a, b)] = Math.Min(a, b);
                    }
                }
            }
        }

        var result = ImmutableArray.CreateBuilder<CandidateCluster>();
        foreach (var group in Enumerable.Range(0, ordered.Length).GroupBy(Find).OrderBy(g => g.Key))
        {
            var indexes = group.ToArray();
            if (indexes.Length < 2 && !includeSingletons)
            {
                continue;
            }

            var representative = indexes.OrderByDescending(i => ordered[i].Evidence.Confidence)
                .ThenByDescending(i => EvidenceScore(ordered[i]))
                .ThenBy(i => ordered[i].CreatedAt)
                .ThenBy(i => ordered[i].Id, StringComparer.Ordinal)
                .First();
            var members = indexes.Select(i => new CandidateClusterMember(
                ordered[i].Id, ordered[i].State.ToString(), ordered[i].Evidence.SourceLocator, ordered[i].Evidence.ExtractionMethod,
                ordered[i].Evidence.Confidence, ordered[i].Evidence.SuggestedOwner,
                i == representative ? 1.0 : Math.Round(Jaccard(tokens[i], tokens[representative]), 2)))
                .OrderByDescending(m => m.Id == ordered[representative].Id).ThenByDescending(m => m.Confidence).ThenBy(m => m.Id, StringComparer.Ordinal)
                .ToImmutableArray();
            result.Add(new CandidateCluster(
                ClusterId(ordered[indexes[0]]),
                ordered[representative].Draft.Kind.ToString(),
                indexes.Length,
                CandidateService.ToDto(ordered[representative]),
                members,
                [.. indexes.Select(i => ordered[i].Evidence.SuggestedOwner).Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                [.. indexes.Select(i => ordered[i].Evidence.ExtractionMethod).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]));
        }

        return [.. result.OrderByDescending(c => c.Size).ThenBy(c => c.Id, StringComparer.Ordinal)];
    }

    /// <summary>Candidate ids are <c>cnd_</c>-prefixed hashes; the cluster id reuses the smallest member's hash.</summary>
    private static string ClusterId(Candidate smallest) => "clu_" + (smallest.Id.StartsWith("cnd_", StringComparison.Ordinal) ? smallest.Id[4..] : smallest.Id);

    /// <summary>How well a candidate is evidenced: an owner, a rationale, scope hints and a precise source locator each count.</summary>
    private static int EvidenceScore(Candidate c) =>
        (string.IsNullOrWhiteSpace(c.Evidence.SuggestedOwner) ? 0 : 1)
        + (string.IsNullOrWhiteSpace(c.Draft.Rationale) ? 0 : 1)
        + (c.Draft.ScopeHints.IsEmpty ? 0 : 1)
        + (c.Evidence.SourceLocator.Contains('@', StringComparison.Ordinal) || c.Evidence.SourceLocator.Contains('#', StringComparison.Ordinal) ? 1 : 0)
        + (c.State is CandidateState.Approved or CandidateState.MergedAsRecord ? 1 : 0);

    private static HashSet<string> Tokens(Candidate c) => Normalize($"{c.Draft.Title} {c.Draft.Statement}");

    /// <summary>Lower-case, split on non-alphanumerics, drop stopwords and one-letter tokens, fold a trailing plural s.</summary>
    public static HashSet<string> Normalize(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var word = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Append(' '))
        {
            if (char.IsLetterOrDigit(ch))
            {
                word.Append(ch);
                continue;
            }

            if (word.Length > 0)
            {
                var token = word.ToString();
                word.Clear();
                if (token.Length > 3 && token.EndsWith('s') && !token.EndsWith("ss", StringComparison.Ordinal))
                {
                    token = token[..^1];
                }

                if (token.Length > 1 && !Stopwords.Contains(token))
                {
                    set.Add(token);
                }
            }
        }

        return set;
    }

    public static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var intersection = a.Count(b.Contains);
        return intersection / (double)(a.Count + b.Count - intersection);
    }
}
