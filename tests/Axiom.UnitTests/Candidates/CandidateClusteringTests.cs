using System.Collections.Immutable;
using Axiom.Application.Candidates;
using Axiom.Application.Common;
using Axiom.Domain.Governance;
using Microsoft.Extensions.Options;

namespace Axiom.UnitTests.Candidates;

public class CandidateClusteringTests
{
    private const string Org = "acme";
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private readonly AxiomPrincipal _reader = new("user:rita", "Rita", Org, [Role.Reader], []);

    private static Candidate Make(string hash, string title, string statement, double confidence = 0.8, string? owner = null, string source = "docs/a.md", RecordKind kind = RecordKind.Decision, string rationale = "") =>
        new()
        {
            Id = Candidate.IdFor(Org, hash),
            OrganizationId = Org,
            Evidence = new CandidateEvidence(source, hash, "adr-markdown/nygard@1", confidence, owner),
            Draft = new CandidateDraft(kind, title, statement, rationale, ImmutableSortedDictionary<string, ImmutableArray<string>>.Empty),
            CreatedAt = Now,
        };

    private static List<Candidate> Sample() =>
    [
        Make("a1", "Use PostgreSQL for services", "All services use PostgreSQL as their datastore.", 0.7, source: "adr/0001.md"),
        Make("a2", "Use PostgreSQL", "All services should use PostgreSQL as the datastore.", 0.9, "platform", "pr#12", rationale: "Because."),
        Make("a3", "PostgreSQL as datastore for service", "Services use PostgreSQL datastore.", 0.6, source: "docs/db.md"),
        Make("b1", "Publish events to Kafka", "Domain events are published to Kafka topics.", 0.8),
        Make("c1", "Use PostgreSQL", "All services should use PostgreSQL as the datastore.", 0.9, kind: RecordKind.Standard),
    ];

    [Fact]
    public void Near_duplicates_of_the_same_kind_form_one_cluster_with_the_best_candidate_as_representative()
    {
        var clusters = CandidateClusteringService.Cluster(Sample(), 0.5);

        var cluster = Assert.Single(clusters);
        Assert.Equal(3, cluster.Size);
        Assert.Equal(Candidate.IdFor(Org, "a2"), cluster.Representative.Id);
        Assert.False(cluster.Representative.Authoritative);
        Assert.Equal(1.0, cluster.Members[0].SimilarityToRepresentative);
        Assert.Equal(["platform"], cluster.SuggestedOwners.AsEnumerable());
    }

    [Fact]
    public void A_different_kind_is_never_clustered_and_singletons_are_optional()
    {
        var all = CandidateClusteringService.Cluster(Sample(), 0.5, includeSingletons: true);

        Assert.Equal(3, all.Length);
        Assert.Equal([3, 1, 1], all.Select(c => c.Size).ToArray());
        Assert.DoesNotContain(all, c => c.Kind == "Standard" && c.Size > 1);
    }

    [Fact]
    public void Clustering_is_deterministic_and_independent_of_input_order()
    {
        var expected = Describe(CandidateClusteringService.Cluster(Sample(), 0.5, true));
        for (var seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed);
            var shuffled = Sample().OrderBy(_ => random.Next()).ToList();
            Assert.Equal(expected, Describe(CandidateClusteringService.Cluster(shuffled, 0.5, true)));
        }

        Assert.Equal(expected, Describe(CandidateClusteringService.Cluster(Sample().AsEnumerable().Reverse().ToList(), 0.5, true)));
        Assert.Equal(expected, Describe(CandidateClusteringService.Cluster(Sample(), 0.5, true)));
    }

    [Fact]
    public void Cluster_ids_are_stable_when_a_member_with_a_larger_id_joins()
    {
        var before = CandidateClusteringService.Cluster(Sample(), 0.5).Single();
        var extra = Make("a4", "Use PostgreSQL datastore", "Services use PostgreSQL as datastore.");
        var after = CandidateClusteringService.Cluster([.. Sample(), extra], 0.5).Single();

        Assert.Equal(4, after.Size);
        if (string.CompareOrdinal(extra.Id, before.Members.Min(m => m.Id)!) > 0)
        {
            Assert.Equal(before.Id, after.Id);
        }
    }

    [Fact]
    public void A_higher_threshold_splits_looser_matches()
    {
        Assert.Single(CandidateClusteringService.Cluster(Sample(), 0.5));
        Assert.All(CandidateClusteringService.Cluster(Sample(), 0.99), c => Assert.True(c.Size < 3));
        Assert.Empty(CandidateClusteringService.Cluster(Sample(), 1.01));
    }

    [Fact]
    public void Normalization_ignores_case_punctuation_stopwords_and_plural_s()
    {
        Assert.Equal(CandidateClusteringService.Normalize("Use the Databases, for all services!"), CandidateClusteringService.Normalize("database SERVICE"));
        Assert.Equal(0, CandidateClusteringService.Jaccard([], ["x"]));
    }

    [Fact]
    public async Task The_service_reads_all_candidates_never_mutates_them_and_requires_read_access()
    {
        var store = new InMemoryCandidateStore();
        store.Items.AddRange(Sample());
        var snapshot = store.Items.ToList();
        var service = new CandidateClusteringService(store, Options.Create(new CandidateClusteringOptions { Threshold = 0.5 }));

        var result = await service.ClusterAsync(_reader, false, default);

        Assert.Single(result.Clusters);
        Assert.Equal(5, result.CandidatesCompared);
        Assert.False(result.Truncated);
        Assert.Equal(snapshot, store.Items);
        Assert.Empty(store.Events);
        await Assert.ThrowsAnyAsync<Exception>(() => service.ClusterAsync(new AxiomPrincipal("x", null, Org, [], []), false, default));
    }

    [Fact]
    public async Task The_comparison_is_bounded_and_reports_truncation()
    {
        var store = new InMemoryCandidateStore();
        store.Items.AddRange(Sample());
        var service = new CandidateClusteringService(store, Options.Create(new CandidateClusteringOptions { Threshold = 0.5, MaxCandidates = 2 }));

        var result = await service.ClusterAsync(_reader, true, default);

        Assert.Equal(2, result.CandidatesCompared);
        Assert.True(result.Truncated);
    }

    private static string Describe(ImmutableArray<CandidateCluster> clusters) =>
        string.Join('|', clusters.Select(c => $"{c.Id}:{c.Representative.Id}:{string.Join(',', c.Members.Select(m => m.Id))}"));
}
