using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using Axiom.Domain.Governance;
using Axiom.Domain.Resolution;
using Xunit.Abstractions;

namespace Axiom.Benchmarks;

/// <summary>
/// Task 3.10: latency of the deterministic resolver at the reference scale of requirements.md
/// ("Scale baseline"): 1,000 repositories, 10,000 components, 25,000 governance records including history,
/// 100 concurrent agent sessions. The corpus is synthetic and seeded, so runs are comparable.
/// The documented target is p95 deterministic preflight &lt; 1.5 s; the resolver is only one step of preflight,
/// so it is asserted against that whole budget and the measured headroom is recorded in
/// docs/operations/resolver-benchmark.md. Exclude from quick runs with <c>--filter "Category!=Benchmark"</c>.
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class ResolverBenchmarkTests(ITestOutputHelper output)
{
    private const int Seed = 20261018;
    private const int Repositories = 1_000;
    private const int ComponentsPerRepository = 10;
    private const int Systems = 100;
    private const int Domains = 20;
    private const int TotalRecords = 25_000;
    private const int ConcurrentSessions = 100;
    private const int NormalLoadInFlight = 4;
    private const int SequentialSamples = 100;
    private const double TargetP95Milliseconds = 1_500;

    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static readonly Lazy<ImmutableArray<RecordRevision>> Corpus = new(BuildCorpus);

    [Fact]
    public void Corpus_has_the_reference_scale_and_is_reproducible()
    {
        var first = Corpus.Value;
        var second = BuildCorpus();

        Assert.Equal(TotalRecords, first.Length);
        Assert.Equal(first.Select(r => r.Record.ContentHash), second.Select(r => r.Record.ContentHash));
        Assert.InRange(first.Count(r => r.Record.Status == LifecycleStatus.Accepted), 4_000, 7_000);
    }

    [Fact]
    public void Resolution_is_deterministic_and_independent_of_record_order()
    {
        var change = ChangeFor(new Random(Seed), 7);
        var ordered = Resolve(Corpus.Value, change);
        var shuffled = Resolve([.. Corpus.Value.OrderBy(_ => new Random(Seed).Next())], change);
        var reversed = Resolve([.. Corpus.Value.Reverse()], change);

        Assert.NotEmpty(ordered.Applicable);
        Assert.Equal(Signature(ordered), Signature(Resolve(Corpus.Value, change)));
        Assert.Equal(Signature(ordered), Signature(reversed));
        Assert.Equal(Signature(ordered), Signature(shuffled));
    }

    [Fact]
    public void Sequential_resolution_p95_meets_the_preflight_target()
    {
        var records = Corpus.Value;
        var random = new Random(Seed + 1);
        Resolve(records, ChangeFor(random, 0)); // JIT warm-up, not measured
        var samples = new double[SequentialSamples];

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = Measure(() => Resolve(records, ChangeFor(random, i)));
        }

        var report = Percentiles.Of(samples);
        output.WriteLine($"sequential x{SequentialSamples} over {records.Length} records: {report}");
        Assert.True(report.P95 < TargetP95Milliseconds, $"p95 {report.P95:F1} ms exceeds {TargetP95Milliseconds} ms ({report})");
    }

    /// <summary>Normal load: 10,000 evaluations/day is ~0.1 per second on average, so a handful of resolutions in flight per replica is already a generous peak.</summary>
    [Fact]
    public async Task Concurrent_resolution_at_four_in_flight_meets_the_preflight_target()
    {
        var report = await RunConcurrentAsync(NormalLoadInFlight, 8);
        Assert.True(report.P95 < TargetP95Milliseconds, $"p95 {report.P95:F1} ms exceeds {TargetP95Milliseconds} ms ({report})");
    }

    /// <summary>
    /// Worst case: all 100 reference agent sessions resolve in the same instant on one replica. Recorded, and only
    /// bounded by the semantic-preflight budget (5 s); a burst above the normal-load target is absorbed by replicas.
    /// </summary>
    [Fact]
    public async Task Burst_of_all_reference_agent_sessions_stays_within_the_semantic_preflight_budget()
    {
        var report = await RunConcurrentAsync(ConcurrentSessions, 1);
        Assert.True(report.P95 < 5_000, $"burst p95 {report.P95:F1} ms exceeds 5000 ms ({report})");
    }

    private async Task<Percentiles> RunConcurrentAsync(int sessions, int perSession)
    {
        var records = Corpus.Value;
        Resolve(records, ChangeFor(new Random(Seed), 0)); // warm-up
        var samples = new double[sessions * perSession];
        var started = Stopwatch.GetTimestamp();

        await Task.WhenAll(Enumerable.Range(0, sessions).Select(session => Task.Run(() =>
        {
            var random = new Random(Seed + 100 + session);
            for (var i = 0; i < perSession; i++)
            {
                samples[(session * perSession) + i] = Measure(() => Resolve(records, ChangeFor(random, i)));
            }
        })));

        var wall = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var report = Percentiles.Of(samples);
        output.WriteLine($"concurrent {sessions} sessions x{perSession} on {Environment.ProcessorCount} cores, wall {wall:F0} ms: {report}");
        return report;
    }

    private static ResolutionResult Resolve(IReadOnlyCollection<RecordRevision> records, EvaluationScope change) =>
        DecisionResolver.Resolve(new ResolutionInput(records, change, Now, HardGate: false));

    private static double Measure(Action action)
    {
        var start = Stopwatch.GetTimestamp();
        action();
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private static string Signature(ResolutionResult result) =>
        string.Join(",", result.Applicable.Select(a => $"{a.Revision.Id}:{a.Importance}:{a.IsEffective}").Order(StringComparer.Ordinal));

    private static EvaluationScope ChangeFor(Random random, int salt)
    {
        var repository = random.Next(Repositories);
        var component = (repository * ComponentsPerRepository) + random.Next(ComponentsPerRepository);
        var system = repository % Systems;
        return EvaluationScope.Empty
            .With(ScopeDimension.Organization, "org:acme")
            .With(ScopeDimension.Domain, $"domain:d{system % Domains}")
            .With(ScopeDimension.System, $"system:s{system}")
            .With(ScopeDimension.Component, $"component:c{component}")
            .With(ScopeDimension.Repository, $"repo:r{repository}")
            .With(ScopeDimension.Path, $"src/m{(salt + component) % 40}/Handler{salt % 7}.cs");
    }

    private static ImmutableArray<RecordRevision> BuildCorpus()
    {
        var random = new Random(Seed);
        var records = ImmutableArray.CreateBuilder<RecordRevision>(TotalRecords);
        var accepted = new List<string>();

        for (var i = 0; i < TotalRecords; i++)
        {
            var id = string.Create(CultureInfo.InvariantCulture, $"ARCH-{i:D5}");
            var roll = random.Next(100);

            // 20% current (accepted); the rest is history that the resolver must scan and discard.
            var status = roll < 20 ? LifecycleStatus.Accepted
                : roll < 70 ? LifecycleStatus.Superseded
                : roll < 85 ? LifecycleStatus.Deprecated
                : roll < 95 ? LifecycleStatus.Rejected
                : LifecycleStatus.Proposed;
            var (scope, level) = ScopeFor(random, i);
            var relations = ImmutableArray.CreateBuilder<Relation>();
            if (status == LifecycleStatus.Accepted)
            {
                if (accepted.Count > 0 && random.Next(100) < 4)
                {
                    relations.Add(new Relation(RelationKind.Refines, accepted[random.Next(accepted.Count)]));
                }

                accepted.Add(id);
            }

            records.Add(new RecordRevision(
                new GovernanceRecord
                {
                    Id = id,
                    Kind = RecordKind.Decision,
                    SchemaVersion = "axiom.io/v1",
                    Title = $"Synthetic decision {i}",
                    Owners = ["platform-architecture"],
                    Status = status,
                    Scope = scope,
                    Authority = new Authority(level, true),
                    Enforcement = new Enforcement(EnforcementLevel.RequireReview, []),
                    Statement = $"Statement {i}",
                    Forbidden = [$"forbidden approach {i}"],
                    Relations = [.. relations],
                    Validity = Validity.Unbounded,
                    ContentHash = $"hash-{i}",
                },
                new SourceProvenance("governance", "abc123", $"decisions/{id}.md", "blob"),
                1));
        }

        return records.MoveToImmutable();
    }

    private static (Scope Scope, AuthorityLevel Level) ScopeFor(Random random, int i)
    {
        var repository = random.Next(Repositories);
        var system = repository % Systems;
        var component = (repository * ComponentsPerRepository) + random.Next(ComponentsPerRepository);
        return random.Next(100) switch
        {
            < 5 => (Scope.Of((ScopeDimension.Organization, ["org:acme"])), AuthorityLevel.Organization),
            < 15 => (Scope.Of((ScopeDimension.Domain, [$"domain:d{system % Domains}"])), AuthorityLevel.Domain),
            < 30 => (Scope.Of((ScopeDimension.System, [$"system:s{system}"])), AuthorityLevel.System),
            < 55 => (Scope.Of((ScopeDimension.Component, [$"component:c{component}"])), AuthorityLevel.Component),
            < 80 => (Scope.Of((ScopeDimension.Repository, [$"repo:r{repository}"])), AuthorityLevel.Repository),
            _ => (Scope.Of((ScopeDimension.Repository, [$"repo:r{repository}"]), (ScopeDimension.Path, [$"src/m{i % 40}/**"])), AuthorityLevel.Repository),
        };
    }
}

internal readonly record struct Percentiles(double P50, double P95, double P99, double Max, int Count)
{
    public static Percentiles Of(double[] samples)
    {
        var sorted = samples.Order().ToArray();
        double At(double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
        return new Percentiles(At(0.50), At(0.95), At(0.99), sorted[^1], sorted.Length);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"n={Count} p50={P50:F1} ms p95={P95:F1} ms p99={P99:F1} ms max={Max:F1} ms");
}
