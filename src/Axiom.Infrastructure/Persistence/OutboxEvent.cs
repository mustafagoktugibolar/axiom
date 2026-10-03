using System.Diagnostics;
using System.Text.Json;
using Axiom.Application.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Axiom.Infrastructure.Persistence;

/// <summary>Row of the transactional outbox (docs/04-contracts/event-contracts.md).</summary>
public sealed class OutboxEvent
{
    public required string EventId { get; init; }

    public required string OrganizationId { get; init; }

    public required string EventType { get; init; }

    public required string SchemaVersion { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string? Traceparent { get; init; }

    /// <summary>The <c>data</c> member of the envelope as JSON.</summary>
    public required string Data { get; init; }

    public DateTimeOffset? DispatchedAt { get; set; }

    public int Attempts { get; set; }
}

internal sealed class OutboxEventConfiguration : IEntityTypeConfiguration<OutboxEvent>
{
    public void Configure(EntityTypeBuilder<OutboxEvent> builder)
    {
        builder.ToTable("outbox_event");
        // The event ID is derived from what the event is about, so a retried transaction cannot enqueue it twice.
        builder.HasKey(e => new { e.OrganizationId, e.EventId });
        builder.Property(e => e.OrganizationId).HasColumnName("organization_id").HasMaxLength(128);
        builder.Property(e => e.EventId).HasColumnName("event_id").HasMaxLength(64);
        builder.Property(e => e.EventType).HasColumnName("event_type").HasMaxLength(128);
        builder.Property(e => e.SchemaVersion).HasColumnName("schema_version").HasMaxLength(16);
        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at");
        builder.Property(e => e.Traceparent).HasColumnName("traceparent").HasMaxLength(64);
        builder.Property(e => e.Data).HasColumnName("data").HasColumnType("jsonb");
        builder.Property(e => e.DispatchedAt).HasColumnName("dispatched_at");
        builder.Property(e => e.Attempts).HasColumnName("attempts");
        builder.HasIndex(e => e.OccurredAt).HasDatabaseName("ix_outbox_event_pending").HasFilter("dispatched_at IS NULL");
    }
}

/// <summary>
/// Adds events to the current <see cref="AxiomDbContext"/> unit of work, so they commit atomically with
/// the state change that caused them. An event already queued under the same ID is left as is.
/// </summary>
public sealed class EfEventOutbox(AxiomDbContext db) : IEventOutbox
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Enqueue(IntegrationEvent integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var set = db.Set<OutboxEvent>();
        if (set.Local.Any(e => e.EventId == integrationEvent.EventId && e.OrganizationId == integrationEvent.OrganizationId)
            || set.Any(e => e.EventId == integrationEvent.EventId && e.OrganizationId == integrationEvent.OrganizationId))
        {
            return;
        }

        set.Add(new OutboxEvent
        {
            EventId = integrationEvent.EventId,
            OrganizationId = integrationEvent.OrganizationId,
            EventType = integrationEvent.EventType,
            SchemaVersion = IntegrationEvent.SchemaVersion,
            OccurredAt = integrationEvent.OccurredAt,
            Traceparent = Activity.Current?.Id,
            Data = JsonSerializer.Serialize(integrationEvent.Data, JsonOptions),
        });
    }
}
