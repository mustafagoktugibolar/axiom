using System.Collections.Immutable;
using Axiom.Application.Catalog;
using Axiom.Application.Common;
using Axiom.Application.Coverage;
using Axiom.Application.Evaluation;
using Axiom.Domain.Catalog;
using Axiom.Domain.Governance;
using Axiom.UnitTests.Evaluation;
using Axiom.UnitTests.Governance;
using Microsoft.Extensions.Time.Testing;

namespace Axiom.UnitTests.Coverage;

public class GovernanceCoverageServiceTests
{
    private const string Org = "acme";
    private readonly FakeSnapshots _snapshots = new();
    private readonly FakeGraph _graph = new();
    private readonly FakeDirectory _directory = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly AxiomPrincipal _reader = Principals.With(Org, Role.Reader);
    private readonly GovernanceCoverageService _service;

    public GovernanceCoverageServiceTests() =>
        _service = new GovernanceCoverageService(_directory, new ChangeScopeResolver(_graph), _snapshots, _time);

    private void Repo(string name, params string[] systems)
    {
        var repo = EntityRef.Parse("repo:" + name);
        _directory.Repositories.Add(repo);
        _graph.Bind(new RepositoryTopology(repo, [], [.. systems.Select(s => EntityRef.Parse("system:" + s))], [], [], [], [], [], [], []));
    }

    private static Scope ForRepo(string name) => Scope.Of((ScopeDimension.Repository, [name]));

    private static readonly RuleBinding Rule = new("no-new-sql", EnforcementLevel.Block, ImmutableSortedDictionary<string, string>.Empty);

    [Fact]
    public async Task Counts_applicable_records_and_bound_rules_per_repository()
    {
        Repo("gateway");
        Repo("billing");
        _snapshots.Publish(Org,
            TestRecords.Decision("ORG-001", scope: Scope.Unrestricted, rules: [Rule]),
            TestRecords.Decision("GW-001", scope: ForRepo("gateway"), rules: [Rule, new RuleBinding("max-lines", EnforcementLevel.Warn, ImmutableSortedDictionary<string, string>.Empty)]),
            TestRecords.Decision("DRAFT-001", LifecycleStatus.Proposed, scope: ForRepo("gateway")));

        var report = await _service.GetAsync(_reader, CoverageFilter.All, 0, 50, default);

        var gateway = report.Items.Single(i => i.Repository == "repo:gateway");
        Assert.Equal(CoverageStatus.Covered, gateway.Status);
        Assert.Equal(2, gateway.ApplicableRecords);
        Assert.Equal(3, gateway.BoundRules);
        Assert.Equal(["GW-001", "ORG-001"], gateway.RecordIds.AsEnumerable());
        var billing = report.Items.Single(i => i.Repository == "repo:billing");
        Assert.Equal(1, billing.ApplicableRecords);
        Assert.Equal(1, billing.BoundRules);
        Assert.Equal(2, report.TotalRepositories);
        Assert.False(report.HasMore);
        Assert.Equal(new CoverageSummary(2, 0, 0, 2, 3, 4), report.Summary);
    }

    [Fact]
    public async Task Repositories_with_no_applicable_accepted_record_are_uncovered()
    {
        Repo("gateway");
        Repo("orphan");
        _snapshots.Publish(Org,
            TestRecords.Decision("GW-001", scope: ForRepo("gateway")),
            TestRecords.Decision("OLD-001", LifecycleStatus.Superseded, scope: ForRepo("orphan")),
            TestRecords.Decision("PROP-001", LifecycleStatus.Proposed, scope: ForRepo("orphan")));

        var report = await _service.GetAsync(_reader, CoverageFilter.Uncovered, 0, 50, default);

        var orphan = Assert.Single(report.Items);
        Assert.Equal("repo:orphan", orphan.Repository);
        Assert.Equal(CoverageStatus.Uncovered, orphan.Status);
        Assert.Equal(0, orphan.ApplicableRecords);
        Assert.Equal(1, report.Summary.Uncovered);
        Assert.Equal(1, report.Summary.Covered);
    }

    [Fact]
    public async Task Repositories_whose_records_are_all_past_review_are_flagged_stale()
    {
        Repo("gateway");
        Repo("billing");
        var overdue = new Validity(null, null, new DateOnly(2026, 6, 1));
        _snapshots.Publish(Org,
            TestRecords.Decision("GW-001", scope: ForRepo("gateway"), validity: overdue),
            TestRecords.Decision("GW-002", scope: ForRepo("gateway"), validity: overdue),
            TestRecords.Decision("BL-001", scope: ForRepo("billing"), validity: overdue),
            TestRecords.Decision("BL-002", scope: ForRepo("billing")));

        var report = await _service.GetAsync(_reader, CoverageFilter.Gaps, 0, 50, default);

        var stale = Assert.Single(report.Items);
        Assert.Equal("repo:gateway", stale.Repository);
        Assert.Equal(CoverageStatus.AllStale, stale.Status);
        Assert.Equal(2, stale.StaleRecords);
        Assert.Equal(1, report.Summary.AllStale);
        Assert.Equal(1, report.Summary.Covered);
    }

    [Fact]
    public async Task A_record_scoped_to_another_system_does_not_cover_the_repository()
    {
        Repo("gateway", "payments");
        _snapshots.Publish(Org, TestRecords.Decision("SYS-001", scope: Scope.Of((ScopeDimension.System, ["checkout"]))));

        var report = await _service.GetAsync(_reader, CoverageFilter.All, 0, 50, default);

        // The repository declares system "payments"; the record is for "checkout", so it does not apply.
        Assert.Equal(CoverageStatus.Uncovered, report.Items[0].Status);
    }

    [Fact]
    public async Task The_report_is_paged_and_bounded()
    {
        for (var i = 0; i < 5; i++)
        {
            Repo("r" + i);
        }

        _snapshots.Publish(Org, TestRecords.Decision("ORG-001"));

        var first = await _service.GetAsync(_reader, CoverageFilter.All, 0, 2, default);
        var last = await _service.GetAsync(_reader, CoverageFilter.All, 4, 2, default);
        var huge = await _service.GetAsync(_reader, CoverageFilter.All, -3, 10_000, default);

        Assert.Equal(["repo:r0", "repo:r1"], first.Items.Select(i => i.Repository));
        Assert.True(first.HasMore);
        Assert.Equal(5, first.TotalRepositories);
        Assert.Equal(["repo:r4"], last.Items.Select(i => i.Repository));
        Assert.False(last.HasMore);
        Assert.Equal(0, huge.Skip);
        Assert.Equal(GovernanceCoverageService.MaxPageSize, huge.Take);
    }

    [Fact]
    public async Task Requires_read_rights_and_a_published_snapshot()
    {
        Repo("gateway");
        var nobody = new AxiomPrincipal("user:x", null, Org, [], []);

        var unavailable = await Assert.ThrowsAsync<AxiomException>(() => _service.GetAsync(_reader, CoverageFilter.All, 0, 10, default));
        Assert.Equal(ErrorKind.Unavailable, unavailable.Kind);

        _snapshots.Publish(Org, TestRecords.Decision("ORG-001"));
        var forbidden = await Assert.ThrowsAsync<AxiomException>(() => _service.GetAsync(nobody, CoverageFilter.All, 0, 10, default));
        Assert.Equal(ErrorKind.Forbidden, forbidden.Kind);
    }

    private sealed class FakeDirectory : IRepositoryDirectory
    {
        public List<EntityRef> Repositories { get; } = [];

        public Task<(ImmutableArray<EntityRef> Items, int Total)> ListRepositoriesAsync(string organizationId, int skip, int take, CancellationToken cancellationToken)
        {
            var sorted = Repositories.Order().ToList();
            return Task.FromResult((sorted.Skip(skip).Take(take).ToImmutableArray(), sorted.Count));
        }
    }
}
