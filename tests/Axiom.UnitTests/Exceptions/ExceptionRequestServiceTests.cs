using System.Collections.Immutable;
using Axiom.Application.Common;
using Axiom.Application.Exceptions;
using Axiom.Domain.Governance;
using Axiom.Infrastructure.Governance.Parsing;
using Axiom.UnitTests.Evaluation;
using Axiom.UnitTests.Governance;
using Microsoft.Extensions.Time.Testing;

namespace Axiom.UnitTests.Exceptions;

public class ExceptionRequestServiceTests
{
    private const string Org = "acme";
    private readonly FakeSnapshots _snapshots = new();
    private readonly InMemoryStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly ExceptionRequestService _service;
    private readonly AxiomPrincipal _developer = new("user:alice", "Alice", Org, [Role.Contributor], ["gui-platform"]);
    private readonly AxiomPrincipal _owner = new("user:bob", "Bob", Org, [Role.ExceptionApprover], ["platform-architecture"]);

    public ExceptionRequestServiceTests()
    {
        _service = new ExceptionRequestService(_snapshots, _store, _time);
        _snapshots.Publish(Org,
            TestRecords.Decision("ARCH-042", scope: Scope.Of((ScopeDimension.Repository, ["gateway"]), (ScopeDimension.Path, ["src/**"]))),
            TestRecords.Decision("SEC-001", exemptable: false),
            TestRecords.Decision("ARCH-900", LifecycleStatus.Superseded));
    }

    private ExceptionRequestInput Input(
        string[]? targets = null, Dictionary<string, string[]>? scope = null, int days = 60, string? tracking = "GUI-912", string rationale = "Temporary compatibility while clients migrate.") =>
        new(targets ?? ["ARCH-042"], scope ?? new() { ["repositories"] = ["gateway"], ["paths"] = ["src/Legacy/**"] }, null, rationale, null,
            _time.GetUtcNow().AddDays(days), tracking, ["No new callers."]);

    private static async Task<AxiomException> InvalidAsync(Task<ExceptionRequestDto> call)
    {
        var error = await Assert.ThrowsAsync<AxiomException>(() => call);
        Assert.Equal(ErrorKind.Validation, error.Kind);
        return error;
    }

    [Fact]
    public async Task A_valid_request_is_pending_routed_to_the_target_owners_and_drafts_a_valid_record()
    {
        var dto = await _service.RequestAsync(_developer, Input(), default);

        Assert.Equal("Pending", dto.Status);
        Assert.Equal(["platform-architecture"], dto.RequiredApprovers.AsEnumerable());
        Assert.Equal(ExceptionRequestService.NextStepPending, dto.NextStep);
        Assert.StartsWith("governance/exceptions/EXC-REQ-", dto.DraftRecordPath, StringComparison.Ordinal);
        Assert.Contains(_store.Events, e => e.EventType == EventTypes.ExceptionRequested);

        // The draft is a record the governance repository would accept: schema-valid, and clean under the set rules.
        var parsed = new GovernanceRecordParser().Parse(dto.DraftRecordPath, dto.DraftRecord);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Issues.Select(i => i.Message)));
        Assert.Equal(LifecycleStatus.Proposed, parsed.Record!.Status);
        Assert.Equal(["ARCH-042"], parsed.Record.Exception!.Targets.AsEnumerable());
        Assert.Equal(["src/Legacy/**"], parsed.Record.Scope[ScopeDimension.Path].AsEnumerable());
        Assert.Equal(["gui-platform"], parsed.Record.Owners.AsEnumerable());
    }

    [Fact]
    public async Task An_identical_request_is_the_same_request()
    {
        var first = await _service.RequestAsync(_developer, Input(), default);
        var second = await _service.RequestAsync(_developer, Input(), default);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(_store.Requests);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("nonexemptable")]
    [InlineData("notgoverning")]
    [InlineData("exceptionkind")]
    public async Task Targets_must_be_governing_exemptable_records(string case_)
    {
        var target = case_ switch { "unknown" => "ARCH-404", "nonexemptable" => "SEC-001", "notgoverning" => "ARCH-900", _ => "EXC-1" };
        _snapshots.Publish(Org,
            TestRecords.Decision("ARCH-042", scope: Scope.Of((ScopeDimension.Repository, ["gateway"]))), TestRecords.Decision("SEC-001", exemptable: false),
            TestRecords.Decision("ARCH-900", LifecycleStatus.Superseded),
            TestRecords.Exception("EXC-1", ["ARCH-042"], Scope.Of((ScopeDimension.Repository, ["gateway"])), _time.GetUtcNow().AddDays(-1), _time.GetUtcNow().AddDays(5)));

        await InvalidAsync(_service.RequestAsync(_developer, Input([target], new() { ["repositories"] = ["gateway"] }), default));
    }

    [Fact]
    public async Task A_scope_broader_than_the_target_is_rejected_by_the_repository_rules()
    {
        var error = await InvalidAsync(_service.RequestAsync(_developer, Input(scope: new() { ["repositories"] = ["billing"] }), default));

        Assert.Contains("outside the scope", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_path_outside_the_targets_paths_is_rejected()
    {
        await InvalidAsync(_service.RequestAsync(_developer, Input(scope: new() { ["repositories"] = ["gateway"], ["paths"] = ["docs/**"] }), default));
    }

    [Theory]
    [InlineData(-1, "GUI-912", "Temporary compatibility while clients migrate.")]
    [InlineData(181, "GUI-912", "Temporary compatibility while clients migrate.")]
    [InlineData(30, null, "Temporary compatibility while clients migrate.")]
    [InlineData(30, "GUI-912", "too short")]
    public async Task The_window_tracking_issue_and_rationale_are_mandatory_and_bounded(int days, string? tracking, string rationale)
    {
        await InvalidAsync(_service.RequestAsync(_developer, Input(days: days, tracking: tracking, rationale: rationale), default));
    }

    [Fact]
    public async Task A_blanket_or_malformed_scope_is_rejected()
    {
        await InvalidAsync(_service.RequestAsync(_developer, Input(scope: new()), default));
        await InvalidAsync(_service.RequestAsync(_developer, Input(scope: new() { ["planets"] = ["mars"] }), default));
        await InvalidAsync(_service.RequestAsync(_developer, Input(scope: new() { ["repositories"] = [] }), default));
        await InvalidAsync(_service.RequestAsync(_developer, Input(targets: []), default));
    }

    [Fact]
    public async Task Requesting_needs_the_request_right_and_deciding_the_approve_right()
    {
        var created = await _service.RequestAsync(_developer, Input(), default);

        await Assert.ThrowsAsync<AxiomException>(() => _service.RequestAsync(Principals.With(Org, Role.Reader), Input(), default));
        await Assert.ThrowsAsync<AxiomException>(() => _service.DecideAsync(_developer with { Subject = "user:carol" }, created.Id, true, "Looks fine to me, honestly.", default));
    }

    [Fact]
    public async Task The_requester_cannot_decide_and_only_routed_owners_or_approvers_can()
    {
        var created = await _service.RequestAsync(_developer, Input(), default);
        var selfApprover = _developer with { Roles = [Role.ExceptionApprover], Teams = ["platform-architecture"] };
        var otherTeam = _owner with { Subject = "user:dave", Teams = ["team-web"] };

        Assert.Contains("requester", (await Assert.ThrowsAsync<AxiomException>(() => _service.DecideAsync(selfApprover, created.Id, true, "I approve my own request.", default))).Message, StringComparison.Ordinal);
        Assert.Contains("routed to other owners", (await Assert.ThrowsAsync<AxiomException>(() => _service.DecideAsync(otherTeam, created.Id, true, "Not my team but fine.", default))).Message, StringComparison.Ordinal);

        var platform = new AxiomPrincipal("user:erin", null, Org, [Role.Approver, Role.ExceptionApprover], []);
        Assert.Equal("Approved", (await _service.DecideAsync(platform, created.Id, true, "Platform approver override, reviewed.", default)).Status);
    }

    [Fact]
    public async Task Approval_names_the_approver_and_turns_the_draft_into_an_accepted_valid_record()
    {
        var created = await _service.RequestAsync(_developer, Input(), default);

        var approved = await _service.DecideAsync(_owner, created.Id, true, "Reviewed; migration plan is credible.", default);

        Assert.Equal("Approved", approved.Status);
        Assert.Equal(ExceptionRequestService.NextStepApproved, approved.NextStep);
        Assert.Equal(("platform-architecture", true), (approved.Decision!.Approver, approved.Decision.Approved));
        Assert.Contains(_store.Events, e => e.EventType == EventTypes.ExceptionDecided);

        var parsed = new GovernanceRecordParser().Parse(approved.DraftRecordPath, approved.DraftRecord);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Issues.Select(i => i.Message)));
        Assert.Equal(LifecycleStatus.Accepted, parsed.Record!.Status);
        Assert.Equal(["platform-architecture"], parsed.Record.Exception!.Approvers.AsEnumerable());
        var snapshot = (await _snapshots.GetCurrentAsync(Org, default))!;
        var set = GovernanceSetValidator.Validate([.. snapshot.Revisions.Select(r => r.Record), parsed.Record]);
        Assert.DoesNotContain(set, i => i.Severity == IssueSeverity.Error && i.RecordId == parsed.Record.Id);
    }

    [Fact]
    public async Task A_request_is_decided_once_and_a_rejection_grants_nothing()
    {
        var created = await _service.RequestAsync(_developer, Input(), default);

        var rejected = await _service.DecideAsync(_owner, created.Id, false, "Comply instead; the migration is done.", default);
        var again = await Assert.ThrowsAsync<AxiomException>(() => _service.DecideAsync(_owner, created.Id, true, "Changed my mind about this.", default));

        Assert.Equal(("Rejected", ExceptionRequestService.NextStepRejected), (rejected.Status, rejected.NextStep));
        Assert.Contains("proposed", rejected.DraftRecord, StringComparison.Ordinal);
        Assert.Equal(ErrorKind.Conflict, again.Kind);
        Assert.Equal("Rejected", (await _service.GetAsync(_owner, created.Id, default)).Status);
    }

    [Fact]
    public async Task A_decision_needs_a_rationale_and_an_exception_that_is_not_already_over()
    {
        var created = await _service.RequestAsync(_developer, Input(days: 10), default);

        await Assert.ThrowsAsync<AxiomException>(() => _service.DecideAsync(_owner, created.Id, true, "ok", default));
        _time.Advance(TimeSpan.FromDays(11));
        var late = await Assert.ThrowsAsync<AxiomException>(() => _service.DecideAsync(_owner, created.Id, true, "Approving a very late request.", default));

        Assert.Equal(ErrorKind.Conflict, late.Kind);
    }

    [Fact]
    public async Task Requests_are_visible_to_their_requester_and_to_approvers_only()
    {
        var created = await _service.RequestAsync(_developer, Input(), default);
        var stranger = _developer with { Subject = "user:mallory" };

        Assert.Equal(created.Id, (await _service.GetAsync(_developer, created.Id, default)).Id);
        Assert.Equal(created.Id, (await _service.GetAsync(_owner, created.Id, default)).Id);
        await Assert.ThrowsAsync<AxiomException>(() => _service.GetAsync(stranger, created.Id, default));
        await Assert.ThrowsAsync<AxiomException>(() => _service.ListAsync(_developer, mine: false, null, 0, 10, default));

        var mine = await _service.ListAsync(_developer, mine: true, ExceptionRequestStatus.Pending, 0, 10, default);
        var queue = await _service.ListAsync(_owner, mine: false, ExceptionRequestStatus.Pending, 0, 10, default);
        Assert.Equal((1, 1), (mine.Total, queue.Total));
        Assert.Equal(0, (await _service.ListAsync(_owner, false, ExceptionRequestStatus.Approved, 0, 10, default)).Total);
        await Assert.ThrowsAsync<AxiomException>(() => _service.GetAsync(_owner, "exr_missing", default));
    }

    private sealed class InMemoryStore : IExceptionRequestStore
    {
        public List<StoredExceptionRequest> Requests { get; } = [];

        public List<IntegrationEvent> Events { get; } = [];

        public Task<StoredExceptionRequest?> FindAsync(string organizationId, string requestId, CancellationToken cancellationToken) =>
            Task.FromResult(Requests.FirstOrDefault(r => r.Request.OrganizationId == organizationId && r.Request.Id == requestId));

        public Task<StoredExceptionRequest> AddAsync(ExceptionRequest request, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken)
        {
            var existing = Requests.FirstOrDefault(r => r.Request.Id == request.Id);
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }

            var stored = new StoredExceptionRequest(request, null);
            Requests.Add(stored);
            Events.AddRange(events);
            return Task.FromResult(stored);
        }

        public Task<bool> DecideAsync(ExceptionDecision decision, IReadOnlyList<IntegrationEvent> events, CancellationToken cancellationToken)
        {
            var index = Requests.FindIndex(r => r.Request.Id == decision.RequestId);
            if (Requests[index].Decision is not null)
            {
                return Task.FromResult(false);
            }

            Requests[index] = Requests[index] with { Decision = decision };
            Events.AddRange(events);
            return Task.FromResult(true);
        }

        public Task<(ImmutableArray<StoredExceptionRequest> Items, int Total)> ListAsync(
            string organizationId, string? requester, ExceptionRequestStatus? status, int skip, int take, CancellationToken cancellationToken)
        {
            var items = Requests.Where(r => r.Request.OrganizationId == organizationId && (requester is null || r.Request.Requester == requester) && (status is null || r.Status == status)).ToList();
            return Task.FromResult((items.Skip(skip).Take(take).ToImmutableArray(), items.Count));
        }
    }
}
