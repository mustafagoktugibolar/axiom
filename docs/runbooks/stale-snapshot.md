# Stale governance snapshot

"Stale" has two meanings; check which one applies.

1. **The published snapshot is behind Git** (sync problem): use governance-sync.md.
2. **Records inside the snapshot are overdue for review**: a governance content problem, handled by record owners.

## Symptoms

- Evaluations fail with `NO_PUBLISHED_GOVERNANCE_SNAPSHOT` (organization never synchronized; an Unavailable error, never a verdict) or decisions look outdated.
- `GET /v1/admin/governance-source`: `snapshot` is null, or `snapshot.sourceCommit` differs from `head.sha`.
- `GET /v1/health/governance?skip=0&take=50` reports stale records, ownerless records, deprecated records still referenced, and expiring exceptions;
  the `GovernanceHealthWorker` logs `Governance health for {Organization}: {Stale} stale, ...` hourly and emits `governance.record.stale` events.
- There is no alert on snapshot age yet (listed as a gap in `axiom-alerts.yaml`). `AxiomBlockRateHigh` can be the first visible sign of a wrong snapshot.

## Diagnosis

1. `GET /v1/admin/governance-source` and compare `snapshot.publishedAt`, `snapshot.sourceCommit`, `head.sha`, `head.committedAt`.
2. Behind Git: `kubectl -n axiom logs deploy/axiom-workers --since=1h | grep -E "Governance (published|sync failed|source .* rejected)"`.
3. Snapshot current but records overdue: `GET /v1/health/governance` and give the listed record ids to their owners.
4. `snapshot` null for an organization that should have one: confirm `organizationId` in the source equals the `org` claim of the callers' tokens
   (a mismatch looks like "never synchronized").

## Mitigation

- Sync behind: fix per governance-sync.md; evaluations keep using the last good snapshot, which is fail-safe for hard rules already in force.
- Never synchronized: register the source (`PUT /v1/admin/governance-source` or configuration seed) and wait one interval.
- Overdue records: owners review and update the record in Git (new commit, normal review). The stale counts clear on the next published snapshot.

## Escalate

If the snapshot is current but evaluations still use a different one, collect two `evaluationId`s and their receipts (`GET /v1/receipts/{id}`, field snapshot id)
and escalate to the platform owner.

## Do not

- Do not edit snapshot or record rows to refresh dates. Do not suppress `governance.record.stale` events to make the dashboard quiet.
- Do not re-run evaluations hoping for a different snapshot; the evaluation id is stable per snapshot and cached.
