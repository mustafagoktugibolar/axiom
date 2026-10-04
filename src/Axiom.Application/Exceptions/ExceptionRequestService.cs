using System.Collections.Immutable;
using System.Globalization;
using Axiom.Application.Common;
using Axiom.Application.Evaluation;
using Axiom.Application.Governance;
using Axiom.Domain.Evaluation;
using Axiom.Domain.Governance;

namespace Axiom.Application.Exceptions;

/// <summary>
/// The exception workflow (R10, R11, ADR-0006). A request is validated by the rules that guard the Git
/// repository, routed to the owners of the targets, and decided by a human other than the requester.
/// Approval here never grants anything: an exception waives only once its record is accepted in the
/// governance repository, inside its window and scope. The service produces that record as a draft.
/// </summary>
public sealed class ExceptionRequestService(IGovernanceSnapshotProvider snapshots, IExceptionRequestStore store, TimeProvider time)
{
    public const int MaxTargets = 20;
    public const int MaxScopeValues = 50;
    public const int MinimumRationaleLength = 20;
    public const int MinimumCommentLength = 10;

    /// <summary>Longest exception that can be requested. Longer-lived deviations are decisions, not exceptions.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(180);

    public const string NextStepPending = "Awaiting a decision from the owners of the target records.";
    public const string NextStepApproved =
        "Approved. The exception waives nothing yet: commit the draft record to the governance repository and merge it as accepted; it applies from its start time until it expires.";
    public const string NextStepRejected = "Rejected. Change the code to comply, or submit a new request that addresses the reviewer's comment.";

    public async Task<ExceptionRequestDto> RequestAsync(AxiomPrincipal principal, ExceptionRequestInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(input);
        Authorizer.Demand(principal, AccessRight.RequestException);

        var now = time.GetUtcNow();
        var snapshot = await snapshots.GetCurrentAsync(principal.OrganizationId, cancellationToken)
            ?? throw AxiomException.Unavailable(ErrorCodes.NoPublishedSnapshot, "No governance snapshot has been published for this organization.");

        var targets = Targets(input);
        var scope = Scope(input);
        var rationale = RequestLimits.Text(input.Rationale, "rationale", 4_000)?.Trim() ?? string.Empty;
        if (rationale.Length < MinimumRationaleLength)
        {
            throw AxiomException.Invalid($"'rationale' must explain the deviation in at least {MinimumRationaleLength} characters.");
        }

        var tracking = RequestLimits.OptionalIdentifier(input.TrackingIssue, "trackingIssue")
            ?? throw AxiomException.Invalid("'trackingIssue' is required: a temporary deviation needs a tracked plan to end it.");
        var startsAt = (input.StartsAt ?? now).ToUniversalTime();
        var expiresAt = input.ExpiresAt.ToUniversalTime();
        CheckWindow(startsAt, expiresAt, now);

        var controls = (input.CompensatingControls ?? []).Select(c => RequestLimits.Text(c, "compensatingControls", 500)?.Trim()).OfType<string>().ToImmutableArray();
        var targetRecords = targets.Select(id => snapshot.Find(id)?.Record).ToList();
        for (var i = 0; i < targets.Length; i++)
        {
            var record = targetRecords[i] ?? throw AxiomException.Invalid($"Target '{targets[i]}' is not a governance record in the current snapshot.");
            if (!record.IsAuthoritativeOn(DateOnly.FromDateTime(now.UtcDateTime)))
            {
                throw AxiomException.Invalid($"Target '{targets[i]}' does not govern ({record.Status}); there is nothing to waive.");
            }
        }

        var owners = principal.Teams.Order(StringComparer.OrdinalIgnoreCase).Take(1).DefaultIfEmpty(principal.Subject).ToImmutableArray();
        var approvers = targetRecords.SelectMany(r => r!.Owners).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        var title = RequestLimits.Text(input.Title, "title", 200)?.Trim() is { Length: > 0 } t ? t : $"Exception to {string.Join(", ", targets)}";

        var fingerprint = EvaluationIdentity.Fingerprint(
            principal.OrganizationId, principal.Subject, string.Join(",", targets),
            string.Join(";", scope.Select(kv => kv.Key + "=" + string.Join(",", kv.Value))), rationale,
            input.StartsAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "now", expiresAt.ToString("O", CultureInfo.InvariantCulture), tracking, string.Join("|", controls));
        var id = "exr_" + fingerprint[..24];
        var request = new ExceptionRequest(
            id, principal.OrganizationId, principal.Subject, targets, scope, title, rationale, startsAt, expiresAt, tracking, controls,
            owners, approvers, DraftId(fingerprint), snapshot.Id, now);

        // The same rules that guard the Git repository: unknown or non-exemptable targets, scope broader than the target, bad window.
        var issues = GovernanceSetValidator.Validate([.. targetRecords!, ExceptionRecordDraft.ToRecord(request, LifecycleStatus.Proposed, [])])
            .Where(i => i.Severity == IssueSeverity.Error && i.RecordId == request.DraftId).ToList();
        if (issues.Count > 0)
        {
            throw AxiomException.Invalid("The exception cannot be granted as requested: " + string.Join(" ", issues.Select(i => i.Message)));
        }

        var data = new { requestId = id, targets, requester = principal.Subject, expiresAt, requiredApprovers = approvers };
        var stored = await store.AddAsync(request, [IntegrationEvent.Create(EventTypes.ExceptionRequested, principal.OrganizationId, now, id, data)], cancellationToken);
        return ToDto(stored);
    }

    public async Task<ExceptionRequestDto> DecideAsync(AxiomPrincipal principal, string requestId, bool approve, string comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Authorizer.Demand(principal, AccessRight.ApproveException);
        var stored = await LoadAsync(principal.OrganizationId, requestId, cancellationToken);
        var request = stored.Request;

        if (stored.Decision is not null)
        {
            throw new AxiomException(ErrorKind.Conflict, ErrorCodes.Conflict, "This request has already been decided.");
        }

        if (string.Equals(principal.Subject, request.Requester, StringComparison.Ordinal))
        {
            throw AxiomException.Forbidden("The requester of an exception cannot decide it.");
        }

        var routed = principal.Teams.Intersect(request.RequiredApprovers, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (routed.Count == 0 && !principal.IsInRole(Role.Approver))
        {
            throw AxiomException.Forbidden("This request is routed to other owners: " + string.Join(", ", request.RequiredApprovers));
        }

        var text = RequestLimits.Text(comment, "comment", 4_000)?.Trim() ?? string.Empty;
        if (text.Length < MinimumCommentLength)
        {
            throw AxiomException.Invalid($"A decision needs a rationale of at least {MinimumCommentLength} characters.");
        }

        var now = time.GetUtcNow();
        if (approve && request.ExpiresAt <= now)
        {
            throw new AxiomException(ErrorKind.Conflict, ErrorCodes.Conflict, "The requested exception would already be over; submit a new request.");
        }

        var approver = routed.Count > 0 ? routed[0] : principal.Subject;
        var decision = new ExceptionDecision(request.Id, request.OrganizationId, approve, approver, principal.Subject, text, now);
        var data = new { requestId = request.Id, approved = approve, approver, targets = request.Targets };
        var recorded = await store.DecideAsync(decision, [IntegrationEvent.Create(EventTypes.ExceptionDecided, request.OrganizationId, now, request.Id, data)], cancellationToken);
        if (!recorded)
        {
            throw new AxiomException(ErrorKind.Conflict, ErrorCodes.Conflict, "This request has already been decided.");
        }

        return ToDto(stored with { Decision = decision });
    }

    public async Task<ExceptionRequestDto> GetAsync(AxiomPrincipal principal, string requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var stored = await LoadAsync(principal.OrganizationId, requestId, cancellationToken);
        if (!string.Equals(stored.Request.Requester, principal.Subject, StringComparison.Ordinal) && !CanSeeAll(principal))
        {
            Authorizer.Demand(principal, AccessRight.ApproveException);
        }

        return ToDto(stored);
    }

    public async Task<(ImmutableArray<ExceptionRequestDto> Items, int Total)> ListAsync(
        AxiomPrincipal principal, bool mine, ExceptionRequestStatus? status, int skip, int take, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (mine)
        {
            Authorizer.Demand(principal, AccessRight.RequestException);
        }
        else if (!CanSeeAll(principal))
        {
            Authorizer.Demand(principal, AccessRight.ApproveException);
        }

        var (items, total) = await store.ListAsync(principal.OrganizationId, mine ? principal.Subject : null, status, Math.Max(0, skip), Math.Clamp(take, 1, 200), cancellationToken);
        return ([.. items.Select(ToDto)], total);
    }

    private static bool CanSeeAll(AxiomPrincipal principal) =>
        Authorizer.IsAllowed(principal, AccessRight.ApproveException) || Authorizer.IsAllowed(principal, AccessRight.ReadAudit);

    private async Task<StoredExceptionRequest> LoadAsync(string organizationId, string requestId, CancellationToken cancellationToken) =>
        await store.FindAsync(organizationId, RequestLimits.Identifier(requestId, "id"), cancellationToken)
        ?? throw AxiomException.NotFound($"Exception request '{requestId}'");

    private static void CheckWindow(DateTimeOffset startsAt, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (expiresAt <= now)
        {
            throw AxiomException.Invalid("'expiresAt' is in the past. An exception must end in the future.");
        }

        if (expiresAt <= startsAt)
        {
            throw AxiomException.Invalid("'expiresAt' must be after 'startsAt'.");
        }

        if (expiresAt - startsAt > MaxDuration)
        {
            throw AxiomException.Invalid($"An exception may last at most {MaxDuration.TotalDays:0} days. For a longer deviation, propose a decision that records it.");
        }
    }

    private static ImmutableArray<string> Targets(ExceptionRequestInput input)
    {
        var targets = (input.Targets ?? []).Select(t => RequestLimits.Identifier(t, "targets")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        return targets.Length is >= 1 and <= MaxTargets ? targets : throw AxiomException.Invalid($"Name between 1 and {MaxTargets} target record IDs.");
    }

    private static ImmutableSortedDictionary<string, ImmutableArray<string>> Scope(ExceptionRequestInput input)
    {
        var scope = ImmutableSortedDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.Ordinal);
        foreach (var (key, values) in input.Scope ?? new Dictionary<string, string[]>())
        {
            if (ExceptionScopeKeys.Dimension(key) is not { } dimension)
            {
                throw AxiomException.Invalid($"'{key}' is not a scope dimension. Use one of: {string.Join(", ", ExceptionScopeKeys.Names)}.");
            }

            var cleaned = (values ?? []).Select(v => RequestLimits.Identifier(v, key)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
            if (cleaned.IsEmpty || cleaned.Length > MaxScopeValues)
            {
                throw AxiomException.Invalid($"Scope '{key}' needs between 1 and {MaxScopeValues} values.");
            }

            if (dimension == ScopeDimension.Path)
            {
                cleaned = [.. cleaned.Select(GlobPattern.NormalizePath)];
            }

            scope[ExceptionScopeKeys.Canonical(key)] = cleaned;
        }

        // R10 / ADR-0006: an exception names an exact scope; a blanket waiver is never valid.
        return scope.Count > 0 ? scope.ToImmutable() : throw AxiomException.Invalid("An exception must declare its scope (for example repositories and paths).");
    }

    /// <summary>A placeholder ID matching the record ID pattern (<c>PREFIX-123</c>); the maintainer renames it when committing the record.</summary>
    private static string DraftId(string fingerprint) =>
        "EXC-REQ-" + (uint.Parse(fingerprint[..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    internal static ExceptionRequestDto ToDto(StoredExceptionRequest stored)
    {
        var request = stored.Request;
        var decision = stored.Decision;
        var approved = decision is { Approved: true };
        var draft = ExceptionRecordDraft.Render(request, approved ? "accepted" : "proposed", approved ? [decision!.Approver] : request.RequiredApprovers);
        return new ExceptionRequestDto(
            request.Id, stored.Status.ToString(), request.Requester, request.Targets, request.Scope, request.Title, request.Rationale, request.StartsAt, request.ExpiresAt,
            request.TrackingIssue, request.CompensatingControls, request.RequiredApprovers, ExceptionRecordDraft.PathFor(request.DraftId), draft,
            decision is null ? null : new ExceptionDecisionDto(decision.Approved, decision.Approver, decision.Comment, decision.DecidedAt),
            stored.Status switch { ExceptionRequestStatus.Approved => NextStepApproved, ExceptionRequestStatus.Rejected => NextStepRejected, _ => NextStepPending });
    }
}
