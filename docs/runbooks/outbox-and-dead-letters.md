# Outbox lag and dead-lettered events

Events are written to `axiom.outbox_event` in the same transaction as the change. The `OutboxDispatcherWorker` (in `axiom-workers`) claims rows with a lease
and delivers each to every configured sink (log sink, signed webhooks). A retry re-sends to all sinks, so consumers de-duplicate on the event id.
Defaults (`Axiom:Events`): poll 5 s, batch 50, `MaxAttempts` 8, backoff 5 s doubling to 15 min, lease 2 min. There is **no API** to list or requeue events; use SQL.

## Symptoms

- No shipped alert covers the outbox. Use the metrics: `axiom_events_failed_total{sink}` rising, `axiom_events_dead_lettered_total{event_type}` increasing at all,
  `axiom_events_lag_seconds` (histogram) p95 growing, `axiom_events_delivered_total{sink}` flat.
- Worker logs: Warning `Delivery of event {EventId} to {Sink} failed (attempt {Attempt}); it will be retried.`,
  Error `Event {EventId} ({EventType}) for {Organization} was dead-lettered after {Attempts} attempts; it stays in the outbox for inspection.`,
  Warning `Event dispatch is enabled but no sink is configured under Axiom:Events; events stay in the outbox.`,
  Information `Event dispatch is disabled.` (`Axiom__Events__Enabled` not true).
- Consumers report missing events (for example `evaluation.blocked`, `exception.expiring`, `governance.snapshot.published`).

## Diagnosis

Run in `psql` against the Axiom database (read-only):

```sql
-- backlog and dead letters
SELECT count(*) FILTER (WHERE dispatched_at IS NULL AND dead_lettered_at IS NULL) AS pending,
       count(*) FILTER (WHERE dead_lettered_at IS NOT NULL AND dispatched_at IS NULL) AS dead_lettered,
       min(occurred_at) FILTER (WHERE dispatched_at IS NULL AND dead_lettered_at IS NULL) AS oldest_pending
FROM axiom.outbox_event;

-- dead letters with the reason
SELECT organization_id, event_id, event_type, occurred_at, attempts, dead_lettered_at, last_error
FROM axiom.outbox_event
WHERE dead_lettered_at IS NOT NULL AND dispatched_at IS NULL
ORDER BY dead_lettered_at DESC LIMIT 50;

-- pending rows that are waiting on backoff or a lease
SELECT event_id, event_type, attempts, next_attempt_at, locked_until, last_error
FROM axiom.outbox_event
WHERE dispatched_at IS NULL AND dead_lettered_at IS NULL
ORDER BY occurred_at LIMIT 50;
```

1. `last_error` names the failing sink (`<sink>: <ExceptionType>: <message>`, cut at 1024 characters).
2. Pending count high, `next_attempt_at` null, nothing failing: dispatcher is disabled, has no sink, or waits for the schema (`kubectl -n axiom logs deploy/axiom-workers | grep -i "event dispatch"`).
3. Webhook failures: check the receiver (https only, secret from configuration, never from the event), its availability and signature verification on its side.
4. Many rows with a future `locked_until` and no progress: a replica died mid-batch; leases expire by themselves after `LeaseDuration`.

## Mitigation

1. Fix the sink or configuration first (receiver up, `Axiom__Events__Webhooks__N__*`, `Axiom__Events__Enabled=true`) and restart the workers
   (`kubectl -n axiom rollout restart deploy/axiom-workers`). Pending rows drain by themselves, oldest first.
2. Dead letters are not retried automatically. After the sink is healthy **and you have confirmed the consumer can accept replays** (duplicates are possible,
   and events are at-least-once), requeue them. This is a write; do it in a transaction and limit the scope:

```sql
BEGIN;
UPDATE axiom.outbox_event
SET dead_lettered_at = NULL, attempts = 0, next_attempt_at = NULL, locked_until = NULL
WHERE dead_lettered_at IS NOT NULL AND dispatched_at IS NULL
  AND organization_id = '<org>' AND event_type = '<type>';   -- narrow it; never run unscoped
-- check the row count is what you expect, then:
COMMIT;
```

3. Watch `axiom_events_delivered_total` rise and the dead-letter query return zero rows.
4. An event that must never be delivered (obsolete, receiver permanently gone) may be left dead-lettered; that is the record that it was not sent.

## Escalate

Escalate to the platform owner if events keep dead-lettering with a healthy receiver, if `last_error` is not about a sink (for example database errors), or if the
backlog grows while the dispatcher logs no failures. Bring the three query results and the receiver's logs.

## Do not

- Do not set `dispatched_at` by hand to clear a backlog unless you verified at the sink that each event arrived; a hand-marked event is lost silently.
- Do not delete outbox rows. Do not requeue without a scope filter, and do not requeue into a receiver that cannot de-duplicate on event id.
- Do not raise `MaxAttempts` to hide a permanently broken sink.
