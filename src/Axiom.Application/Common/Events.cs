namespace Axiom.Application.Common;

/// <summary>Event type names from docs/04-contracts/event-contracts.md.</summary>
public static class EventTypes
{
    public const string GovernanceRecordChanged = "governance.record.changed";
    public const string GovernanceSnapshotPublished = "governance.snapshot.published";
    public const string GovernanceRecordStale = "governance.record.stale";
    public const string CatalogEntityChanged = "catalog.entity.changed";
    public const string EvaluationCompleted = "evaluation.completed";
    public const string EvaluationBlocked = "evaluation.blocked";
    public const string ExceptionExpiring = "exception.expiring";
    public const string CandidateDecisionCreated = "candidate.decision.created";
}

/// <summary>
/// An integration event queued in the transactional outbox. Delivery is at-least-once; consumers
/// de-duplicate on <see cref="EventId"/>. <see cref="Data"/> is serialized as the envelope's <c>data</c>.
/// </summary>
public sealed record IntegrationEvent(string EventId, string EventType, string OrganizationId, DateTimeOffset OccurredAt, object Data)
{
    public const string SchemaVersion = "1";

    /// <summary>
    /// Derives the event ID from what the event is about, so re-processing the same source change
    /// produces the same ID and duplicates collapse.
    /// </summary>
    public static IntegrationEvent Create(string eventType, string organizationId, DateTimeOffset occurredAt, string idempotencyKey, object data)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{eventType}\n{organizationId}\n{idempotencyKey}"));
        return new IntegrationEvent("evt_" + Convert.ToHexStringLower(digest.AsSpan(0, 16)), eventType, organizationId, occurredAt, data);
    }
}

/// <summary>Queues events in the same transaction as the state change that caused them.</summary>
public interface IEventOutbox
{
    void Enqueue(IntegrationEvent integrationEvent);
}
