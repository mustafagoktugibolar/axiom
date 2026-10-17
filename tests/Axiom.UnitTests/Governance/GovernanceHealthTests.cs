using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Application.Governance;
using Axiom.Domain.Governance;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Axiom.UnitTests.Governance;

public sealed class GovernanceHealthTests
{
    private const string Org = "acme";
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromDays(14);

    private readonly FakeTimeProvider _time = new(Now);
    private readonly IGovernanceSnapshotProvider _snapshots = Substitute.For<IGovernanceSnapshotProvider>();
    private readonly FakeEventWriter _events = new();

    private static GovernanceSnapshot Snapshot(params GovernanceRecord[] records) =>
        GovernanceSnapshot.Create(Org, "c1", records.Select((r, i) => new RecordRevision(r, new SourceProvenance("repo", "c1", $"{r.Id}.md", "blob"), 1)));

    private static GovernanceRecord Expiring(string id, int days, LifecycleStatus status = LifecycleStatus.Accepted) =>
        TestRecords.Exception(id, ["ARCH-001"], Scope.Unrestricted, Now.AddDays(-30), Now.AddDays(days), status);

    private static IOptions<GovernanceHealthOptions> Options() => Microsoft.Extensions.Options.Options.Create(new GovernanceHealthOptions { ExpiryWindow = Window });

    [Fact]
    public void Records_past_their_review_date_are_stale_but_only_when_accepted()
    {
        var snapshot = Snapshot(
            TestRecords.Decision("ARCH-001", validity: new Validity(null, null, new DateOnly(2026, 5, 31))),
            TestRecords.Decision("ARCH-002", validity: new Validity(null, null, new DateOnly(2026, 6, 1))),
            TestRecords.Decision("ARCH-003", status: LifecycleStatus.Deprecated, validity: new Validity(null, null, new DateOnly(2020, 1, 1))));

        var report = GovernanceHealthCalculator.Compute(snapshot, Now, Window);

        Assert.Equal(["ARCH-001"], report.StaleRecords.Select(i => i.RecordId));
    }

    [Fact]
    public void Records_without_owners_are_listed()
    {
        var snapshot = Snapshot(TestRecords.Decision("ARCH-001", owners: []), TestRecords.Decision("ARCH-002"));

        var report = GovernanceHealthCalculator.Compute(snapshot, Now, Window);

        Assert.Equal(["ARCH-001"], report.RecordsWithoutOwners.Select(i => i.RecordId));
    }

    [Fact]
    public void A_deprecated_record_is_reported_only_while_a_live_record_depends_on_it()
    {
        var snapshot = Snapshot(
            TestRecords.Decision("ARCH-001", status: LifecycleStatus.Deprecated),
            TestRecords.Decision("ARCH-002", status: LifecycleStatus.Deprecated),
            TestRecords.Decision("ARCH-003", status: LifecycleStatus.Deprecated),
            TestRecords.Decision("ARCH-010", relations: [new Relation(RelationKind.Requires, "ARCH-001")]),
            TestRecords.Decision("ARCH-011", relations: [new Relation(RelationKind.Related, "ARCH-002")]),
            TestRecords.Decision("ARCH-012", status: LifecycleStatus.Deprecated, relations: [new Relation(RelationKind.Requires, "ARCH-003")]));

        var report = GovernanceHealthCalculator.Compute(snapshot, Now, Window);

        var item = Assert.Single(report.DeprecatedStillReferenced);
        Assert.Equal("ARCH-001", item.RecordId);
        Assert.Equal(["ARCH-010"], item.ReferencedBy.AsEnumerable());
    }

    [Fact]
    public void Only_accepted_exceptions_inside_the_window_are_expiring()
    {
        var snapshot = Snapshot(
            Expiring("EXC-001", 3),
            Expiring("EXC-002", 14),
            Expiring("EXC-003", 15),
            Expiring("EXC-004", -1),
            Expiring("EXC-005", 3, LifecycleStatus.Proposed));

        var report = GovernanceHealthCalculator.Compute(snapshot, Now, Window);

        Assert.Equal(["EXC-001", "EXC-002"], report.ExpiringExceptions.Select(i => i.RecordId));
        Assert.Equal(Now.AddDays(3), report.ExpiringExceptions[0].DueAt);
    }

    [Fact]
    public async Task Expiry_notice_is_queued_once_per_exception_and_expiry()
    {
        _snapshots.GetCurrentAsync(Org, Arg.Any<CancellationToken>()).Returns(Snapshot(Expiring("EXC-001", 3), Expiring("EXC-002", 40)));
        var notifier = new ExceptionExpiryNotifier(_snapshots, _events, Options(), _time);

        Assert.Equal(1, await notifier.NotifyAsync(Org, CancellationToken.None));
        _time.Advance(TimeSpan.FromHours(6));
        Assert.Equal(0, await notifier.NotifyAsync(Org, CancellationToken.None));

        var notice = Assert.Single(_events.Stored);
        Assert.Equal(EventTypes.ExceptionExpiring, notice.EventType);
        var data = Assert.IsType<ExceptionExpiringData>(notice.Data);
        Assert.Equal("EXC-001", data.ExceptionId);
        Assert.Equal(["ARCH-001"], data.Targets.AsEnumerable());
        Assert.Equal(Now.AddDays(3), data.ExpiresAt);
    }

    [Fact]
    public async Task An_extended_exception_is_announced_again_when_it_nears_its_new_expiry()
    {
        var notifier = new ExceptionExpiryNotifier(_snapshots, _events, Options(), _time);
        _snapshots.GetCurrentAsync(Org, Arg.Any<CancellationToken>()).Returns(Snapshot(Expiring("EXC-001", 3)));
        await notifier.NotifyAsync(Org, CancellationToken.None);

        _snapshots.GetCurrentAsync(Org, Arg.Any<CancellationToken>()).Returns(Snapshot(Expiring("EXC-001", 10)));
        Assert.Equal(1, await notifier.NotifyAsync(Org, CancellationToken.None));
    }

    [Fact]
    public async Task No_snapshot_means_no_notices()
    {
        var notifier = new ExceptionExpiryNotifier(_snapshots, _events, Options(), _time);

        Assert.Equal(0, await notifier.NotifyAsync(Org, CancellationToken.None));
        Assert.Empty(_events.Stored);
    }

    [Fact]
    public async Task Health_summary_has_exact_counts_and_bounded_pages()
    {
        _snapshots.GetCurrentAsync(Org, Arg.Any<CancellationToken>()).Returns(Snapshot(
            [.. Enumerable.Range(1, 5).Select(i => TestRecords.Decision($"ARCH-{i:000}", validity: new Validity(null, null, new DateOnly(2026, 1, 1))))]));
        var service = new GovernanceHealthService(_snapshots, Options(), _time);

        var page = await service.GetAsync(Reader("acme"), skip: 1, take: 2, CancellationToken.None);

        Assert.Equal(5, page.Counts.StaleRecords);
        Assert.Equal(["ARCH-002", "ARCH-003"], page.StaleRecords.Select(i => i.RecordId));
        Assert.Equal(14, page.ExpiryWindowDays);
    }

    [Fact]
    public async Task Health_summary_of_an_organization_without_a_snapshot_is_empty()
    {
        var service = new GovernanceHealthService(_snapshots, Options(), _time);

        var summary = await service.GetAsync(Reader("acme"), 0, 50, CancellationToken.None);

        Assert.Null(summary.SnapshotId);
        Assert.Equal(new GovernanceHealthCounts(0, 0, 0, 0), summary.Counts);
    }

    [Fact]
    public async Task Health_summary_requires_the_read_right()
    {
        var service = new GovernanceHealthService(_snapshots, Options(), _time);
        var nobody = new AxiomPrincipal("user:x", null, Org, [], []);

        await Assert.ThrowsAsync<AxiomException>(() => service.GetAsync(nobody, 0, 50, CancellationToken.None));
    }

    private static AxiomPrincipal Reader(string org) => new("user:rae", null, org, [Role.Reader], []);

    /// <summary>Stores events once per event ID, as the outbox does.</summary>
    private sealed class FakeEventWriter : IGovernanceEventWriter
    {
        public List<IntegrationEvent> Stored { get; } = [];

        public Task<int> WriteAsync(string organizationId, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken)
        {
            var added = 0;
            foreach (var e in events.Where(e => Stored.All(s => s.EventId != e.EventId)))
            {
                Stored.Add(e);
                added++;
            }

            return Task.FromResult(added);
        }
    }
}
