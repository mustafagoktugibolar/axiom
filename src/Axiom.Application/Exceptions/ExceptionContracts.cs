using System.Collections.Immutable;

namespace Axiom.Application.Exceptions;

public enum ExceptionRequestStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>An exception request as a caller submits it. Dimension keys are the plural names used in governance records.</summary>
public sealed record ExceptionRequestInput(
    IReadOnlyCollection<string>? Targets,
    IReadOnlyDictionary<string, string[]>? Scope,
    string? Title,
    string? Rationale,
    DateTimeOffset? StartsAt,
    DateTimeOffset ExpiresAt,
    string? TrackingIssue,
    IReadOnlyCollection<string>? CompensatingControls);

/// <summary>What was asked for. Immutable; the decision about it is a separate, single, append-only fact.</summary>
public sealed record ExceptionRequest(
    string Id,
    string OrganizationId,
    string Requester,
    ImmutableArray<string> Targets,
    ImmutableSortedDictionary<string, ImmutableArray<string>> Scope,
    string Title,
    string Rationale,
    DateTimeOffset StartsAt,
    DateTimeOffset ExpiresAt,
    string? TrackingIssue,
    ImmutableArray<string> CompensatingControls,
    ImmutableArray<string> Owners,
    ImmutableArray<string> RequiredApprovers,
    string DraftId,
    string SnapshotId,
    DateTimeOffset CreatedAt);

public sealed record ExceptionDecision(string RequestId, string OrganizationId, bool Approved, string Approver, string ApproverIdentity, string Comment, DateTimeOffset DecidedAt);

public sealed record StoredExceptionRequest(ExceptionRequest Request, ExceptionDecision? Decision)
{
    public ExceptionRequestStatus Status => Decision is null ? ExceptionRequestStatus.Pending : Decision.Approved ? ExceptionRequestStatus.Approved : ExceptionRequestStatus.Rejected;
}

/// <summary>Append-only store of exception requests and their single decision each (audit on all approvals, design.md §14).</summary>
public interface IExceptionRequestStore
{
    Task<StoredExceptionRequest?> FindAsync(string organizationId, string requestId, CancellationToken cancellationToken);

    /// <summary>Stores the request and queues its events in one transaction. An existing ID returns the stored request unchanged.</summary>
    Task<StoredExceptionRequest> AddAsync(ExceptionRequest request, IReadOnlyList<Common.IntegrationEvent> events, CancellationToken cancellationToken);

    /// <summary>Records the decision and queues its events in one transaction. Returns false when the request already has a decision.</summary>
    Task<bool> DecideAsync(ExceptionDecision decision, IReadOnlyList<Common.IntegrationEvent> events, CancellationToken cancellationToken);

    Task<(ImmutableArray<StoredExceptionRequest> Items, int Total)> ListAsync(
        string organizationId, string? requester, ExceptionRequestStatus? status, int skip, int take, CancellationToken cancellationToken);
}

public sealed record ExceptionDecisionDto(bool Approved, string Approver, string Comment, DateTimeOffset DecidedAt);

/// <summary>An exception request as REST and MCP show it.</summary>
public sealed record ExceptionRequestDto(
    string Id,
    string Status,
    string Requester,
    ImmutableArray<string> Targets,
    ImmutableSortedDictionary<string, ImmutableArray<string>> Scope,
    string Title,
    string Rationale,
    DateTimeOffset StartsAt,
    DateTimeOffset ExpiresAt,
    string? TrackingIssue,
    ImmutableArray<string> CompensatingControls,
    ImmutableArray<string> RequiredApprovers,
    string DraftRecordPath,
    string DraftRecord,
    ExceptionDecisionDto? Decision,
    string NextStep);
