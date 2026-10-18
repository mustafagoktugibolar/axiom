# Governance sync stuck or failing

The `GovernanceSyncWorker` (in `axiom-workers`) checks each registered governance source every
`Axiom:Workers:GovernanceSync:Interval` (default 1 minute), validates the head commit and publishes a snapshot. A rejected
commit leaves the previous snapshot in force.

## Symptoms

- Alert `AxiomGovernanceProjectionLag` (`axiom_ingestion_lag_seconds`). Note: the instrument `axiom.ingestion.lag` is not recorded yet
  (threat-model finding R-10), so this alert may stay silent; rely on the log lines and the source endpoint below.
- Worker log, level Error: `Governance sync failed for {Organization} ({Url}); will retry.` or `Governance sync cycle failed; will retry.`
- Worker log, level Warning: `Governance source for {Organization} at {Commit} was rejected with {Issues} issue(s); the previous snapshot stays in force.`
- Worker log, level Information: `Database schema is not ready ... governance sync waits.` (see schema-readiness.md)
- Success line to look for: `Governance published for {Organization} at {Commit}: {Records} records, {Changed} changed.`
- Governance edits merged in Git do not show up in evaluations.

## Diagnosis

1. Is the worker running and enabled?
   `kubectl -n axiom get pods -l app.kubernetes.io/component=workers` and
   `kubectl -n axiom logs deploy/axiom-workers --since=30m | grep -i "governance"`.
   `Governance sync is disabled.` means `Axiom__Workers__GovernanceSync__Enabled=false`.
2. Compare Git with what is published. `GET /v1/admin/governance-source` returns `source`, `snapshot` (`snapshotId`, `sourceCommit`,
   `publishedAt`, `recordCount`) and `head` (`sha`, `committedAt`, or `error` text when the head cannot be fetched).
   Different `snapshot.sourceCommit` and `head.sha` for longer than a few intervals = sync is behind.
3. `head.error` set: the repository is unreachable or the token is wrong. Follow scm-failure.md (credentials) and check
   `Axiom__Governance__Credentials__<host>__Token` / Secret key `git-token-github`.
4. Rejected commit (Warning log): the repository content fails validation. The issues are in the sync result for that commit; validate locally with the
   governance repository's CI and fix the record on a new commit. Axiom keeps serving the last good snapshot.
5. No log lines at all and no pod restarts: check that the source is registered (`configured: true` in the endpoint response). Sources come from
   `Axiom__Workers__GovernanceSync__Sources__N__*` or `PUT /v1/admin/governance-source`.

## Mitigation

- Fix the cause (token, URL, invalid record on a new commit). The next cycle publishes without any manual step.
- To change the location: `PUT /v1/admin/governance-source` with `{ "repositoryUrl", "branch", "rootPath" }`. It reads the repository first and refuses a
  location that fails or holds no files. Note that a seed in configuration overwrites the stored location at every worker start.
- A worker restart is safe: `kubectl -n axiom rollout restart deploy/axiom-workers`.

## Escalate

Page the platform owner when the worker is healthy, `head.sha` is reachable and valid, and no snapshot is published for more than 15 minutes; or when
errors other than the above (database, serialization) repeat. Include the organization id, `snapshot` and `head` JSON and 30 minutes of worker logs.

## Do not

- Do not insert or update `axiom.governance_*` tables by hand, and do not delete `axiom.sync_checkpoint` to "force" a sync.
- Do not bypass validation by pointing the source at an unreviewed branch to get a change live.
- Do not lower evaluation strictness to compensate for a stale snapshot.
