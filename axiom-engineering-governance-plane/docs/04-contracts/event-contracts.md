# Event Contracts

## Envelope

```json
{
  "eventId": "evt_...",
  "eventType": "governance.record.changed",
  "schemaVersion": "1",
  "organizationId": "acme",
  "occurredAt": "2026-10-03T12:00:00Z",
  "traceparent": "00-...",
  "data": {}
}
```

Consumers must be idempotent on `eventId`.

## `governance.record.changed`

Data:
- record ID;
- kind;
- old/new lifecycle;
- Git repository/commit/path;
- affected scope summary.

## `governance.snapshot.published`

Data:
- snapshot ID;
- source commit;
- record counts;
- validation status.

## `catalog.entity.changed`

Data:
- entity ref;
- change type;
- provenance;
- catalog version.

## `evaluation.completed`

Data:
- evaluation ID;
- stage;
- repo/ref/SHA;
- verdict;
- snapshot ID;
- receipt ID if finalized.

## `evaluation.blocked`

For high-signal integrations/metrics.

## `exception.expiring`

Data:
- exception ID;
- target decisions;
- expiry;
- owners;
- observed recent usage.

## `governance.record.stale`

Data:
- record ID;
- review due;
- owner;
- last applied timestamp.

## Delivery

Events are at-least-once.
Use outbox pattern from source transaction where appropriate.
No consumer may depend on strict global ordering.
