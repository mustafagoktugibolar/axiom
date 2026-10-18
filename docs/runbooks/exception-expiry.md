# Exception expiry wave

An accepted exception record waives a control until its `expiresAt`. After expiry the resolver stops applying it, so violations that were waived start to appear again as findings
(and as blocks for hard controls). Many exceptions expiring around the same time cause a sudden wave of failing evaluations.

## Symptoms

- Alert `AxiomBlockRateHigh` (BLOCK share > 50% for 30 min) after a date boundary.
- Alert `AxiomUnresolvedAuthorityConflicts` or a rise in `axiom_evaluations_total{verdict="BLOCK"}` on repositories that were previously clean.
- `axiom_exception_usage_total` (alert `AxiomExceptionsInHeavyUse`, > 100 in a day) was high just before: heavy use precedes a wave.
- Event `exception.expiring` emitted by the `GovernanceHealthWorker` (window `Axiom:Governance:Health:ExpiryWindow`, default 14 days, checked hourly by default; log line
  `Governance health for {Organization}: ... {Expiring} exceptions expiring; ... {NewExpiring} expiry notice(s) queued.`). Notices are idempotent per exception.
- Teams report findings that mention `expired at <timestamp>` in the resolution reasons.

## Diagnosis

1. `GET /v1/health/governance?skip=0&take=50`: lists expiring exceptions with their expiry dates (also stale records).
2. Group by expiry date and owner; identify exceptions that expired in the last 48 hours: look at the finding messages / `GET /v1/context/{evaluationId}` of a failing evaluation for the exception id.
3. Check whether the event reached owners: outbox query in outbox-and-dead-letters.md filtered by `event_type = 'exception.expiring'`. A dead-lettered notice means owners were not told.
4. Exceptions pending approval: `GET /v1/exceptions/requests?status=...` (requested but not yet merged exceptions waive nothing).

## Mitigation

- Owners decide per exception: fix the code, or request a renewal. Renewal goes through the normal flow (`POST /v1/exceptions/requests`, decision by an independent approver, merged in Git); maximum duration is 180 days and a tracking issue is required.
  Approval waives nothing until the record is merged and the next snapshot is published (governance-sync.md).
- Hard (non-exemptable) controls cannot be waived at all; the fix is in the code.
- For the next cycle: make sure `exception.expiring` has a working sink, and review the `ExpiryWindow` so owners get notice before the date. Stagger renewals instead of one shared expiry date.

## Escalate

Escalate to the governance board / decision owners when a business-critical exception lapsed and cannot be fixed in time; they decide on a new, time-boxed exception through Git.

## Do not

- Do not edit the exception record's `expiresAt` in the database, or in the snapshot, or patch the resolver clock.
- Do not bulk-approve exceptions to stop the wave, and do not approve your own request (the requester cannot decide).
- Do not turn the affected rules to a weaker enforcement mode in order to quiet `AxiomBlockRateHigh`.
