namespace Axiom.Application.Common;

/// <summary>An outbox row claimed for delivery. <see cref="DataJson"/> is the envelope's <c>data</c> member as stored.</summary>
public sealed record OutboxMessage(
    string EventId,
    string OrganizationId,
    string EventType,
    string SchemaVersion,
    DateTimeOffset OccurredAt,
    string? Traceparent,
    string DataJson,
    int Attempts);

/// <summary>
/// A destination for integration events. Delivery is at-least-once, so a sink may see the same
/// <see cref="OutboxMessage.EventId"/> again and consumers de-duplicate on it. A sink signals failure by throwing.
/// </summary>
public interface IEventSink
{
    /// <summary>Stable, non-sensitive name used as a metric and log label.</summary>
    string Name { get; }

    Task DeliverAsync(OutboxMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// The dispatcher's view of the outbox. Claims are leases, so several replicas can drain the outbox
/// without delivering the same row at the same time; a lease that is never released simply expires.
/// </summary>
public interface IOutboxStore
{
    /// <summary>Leases up to <paramref name="limit"/> due, undelivered, not dead-lettered events, oldest first.</summary>
    Task<IReadOnlyList<OutboxMessage>> ClaimAsync(int limit, TimeSpan lease, CancellationToken cancellationToken);

    Task MarkDeliveredAsync(OutboxMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Records a failed attempt and releases the lease. With a <paramref name="retryAt"/> the event becomes
    /// due again then; without one it is dead-lettered and no longer claimed (it stays in the table for inspection).
    /// </summary>
    Task MarkFailedAsync(OutboxMessage message, DateTimeOffset? retryAt, string reason, CancellationToken cancellationToken);
}

/// <summary>Bound from <c>Axiom:Events</c>. Delivery is off until explicitly enabled.</summary>
public sealed class EventsOptions
{
    public const string SectionName = "Axiom:Events";

    public bool Enabled { get; set; }

    /// <summary>Wait between polls when the outbox is drained.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public int BatchSize { get; set; } = 50;

    /// <summary>Attempts (the first included) before an event is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Delay after the first failure; doubles with every further failure up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a claimed event is reserved for one replica; must exceed the slowest delivery.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Emit one structured log line per delivered event.</summary>
    public bool LogSink { get; set; }

    public List<WebhookEndpointOptions> Webhooks { get; } = [];
}

/// <summary>A signed webhook destination. The URL must be https; the secret comes from configuration, never from the event.</summary>
public sealed class WebhookEndpointOptions
{
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string Secret { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}
