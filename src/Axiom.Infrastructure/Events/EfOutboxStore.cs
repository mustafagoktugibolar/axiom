using Axiom.Application.Common;
using Axiom.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Axiom.Infrastructure.Events;

/// <summary>
/// Leases outbox rows with <c>FOR UPDATE SKIP LOCKED</c>, so concurrent dispatcher replicas never claim the
/// same row; the lease column makes a crashed replica's rows claimable again once it expires.
/// </summary>
public sealed class EfOutboxStore(AxiomDbContext db, TimeProvider time) : IOutboxStore
{
    private const int MaxErrorLength = 1024;

    public async Task<IReadOnlyList<OutboxMessage>> ClaimAsync(int limit, TimeSpan lease, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var until = now + lease;

        // One statement selects and leases atomically; rows locked by another replica are skipped, not waited for.
        var rows = await db.Set<OutboxEvent>().FromSqlInterpolated($"""
            UPDATE axiom.outbox_event SET locked_until = {until}
            WHERE (organization_id, event_id) IN (
                SELECT organization_id, event_id FROM axiom.outbox_event
                WHERE dispatched_at IS NULL AND dead_lettered_at IS NULL
                  AND (next_attempt_at IS NULL OR next_attempt_at <= {now})
                  AND (locked_until IS NULL OR locked_until <= {now})
                ORDER BY occurred_at
                LIMIT {limit}
                FOR UPDATE SKIP LOCKED)
            RETURNING *
            """).AsNoTracking().ToListAsync(cancellationToken);

        return [.. rows.OrderBy(r => r.OccurredAt).Select(r => new OutboxMessage(
            r.EventId, r.OrganizationId, r.EventType, r.SchemaVersion, r.OccurredAt, r.Traceparent, r.Data, r.Attempts))];
    }

    public async Task MarkDeliveredAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = time.GetUtcNow();
        await db.Set<OutboxEvent>()
            .Where(e => e.OrganizationId == message.OrganizationId && e.EventId == message.EventId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.DispatchedAt, now)
                .SetProperty(e => e.Attempts, e => e.Attempts + 1)
                .SetProperty(e => e.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(e => e.NextAttemptAt, (DateTimeOffset?)null)
                .SetProperty(e => e.LastError, (string?)null), cancellationToken);
    }

    public async Task MarkFailedAsync(OutboxMessage message, DateTimeOffset? retryAt, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = time.GetUtcNow();
        var text = reason.Length > MaxErrorLength ? reason[..MaxErrorLength] : reason;
        DateTimeOffset? deadLetteredAt = retryAt is null ? now : null;
        await db.Set<OutboxEvent>()
            .Where(e => e.OrganizationId == message.OrganizationId && e.EventId == message.EventId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Attempts, e => e.Attempts + 1)
                .SetProperty(e => e.NextAttemptAt, retryAt)
                .SetProperty(e => e.DeadLetteredAt, deadLetteredAt)
                .SetProperty(e => e.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(e => e.LastError, text), cancellationToken);
    }
}
