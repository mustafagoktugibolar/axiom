# Database unavailable

PostgreSQL holds the governance projection, evaluations, receipts, outbox and catalog. Without it Axiom cannot produce a verdict; it fails closed.

## Symptoms

- `GET /health/ready` returns unhealthy (check `postgresql`, tag `ready`: "The database is unreachable or its schema is not fully migrated" or "The database schema could not be checked"). `GET /health/live` stays healthy by design, so pods are not restarted for a database outage.
- API calls return 503 `GOVERNANCE_UNAVAILABLE` (retryable) with a trace id; MCP returns the retryable unavailable error.
- Alert `AxiomReceiptGenerationFailing` (`axiom_receipt_failures_total`): a receipt append failed.
- Latency alerts (`AxiomPreflightLatencySloBreach`, `AxiomPullRequestLatencySloBreach`) if the database is slow.
- Workers log Information `Database schema is not ready (migrations pending or database unreachable); ... waits.` for sync, health, catalog and event dispatch; the dispatch/sync resume by themselves.

## Diagnosis

1. Pods and readiness: `kubectl -n axiom get pods`; `kubectl -n axiom describe pod <api-pod>` (readiness probe events).
2. Reachability from inside the cluster, with the secret's connection string: `kubectl -n axiom exec deploy/axiom-api -- printenv ConnectionStrings__Axiom` (secret; do not share), then test with `psql`.
3. Database side: provider status, disk full, connection limit (many API replicas times pool size), failover in progress, expired credentials after rotation (secret-rotation.md).
4. If only readiness is red but the database answers: schema not fully migrated (schema-readiness.md).
5. Slow rather than down: `SELECT pid, state, wait_event, now() - query_start AS age, left(query, 80) FROM pg_stat_activity WHERE datname = current_database() ORDER BY age DESC LIMIT 20;`

## Mitigation

- Restore the database service (failover, disk, connection limit). Axiom needs no manual step afterwards: readiness turns green, workers resume on their next tick.
- Connection exhaustion: scale the API down or raise the server limit; do not mask it by raising pool sizes blindly.
- If the connection string changed, update the Secret and restart pods (secret-rotation.md).
- After recovery, run `GET /v1/receipts/chain/verify` once per organization to confirm the chain is intact (restores in particular; see receipt-chain.md), and check the outbox backlog (outbox-and-dead-letters.md).
- Callers: retry with backoff on 503 `GOVERNANCE_UNAVAILABLE`. CI gates should fail the check, not skip it.

## Escalate

Page the database owner at once for data loss, replication problems or a restore. Page the platform owner if the database is healthy and readiness stays red for more than 10 minutes.

## Do not

- Do not run `--migrate` or manual DDL against a database in an unknown state; do not edit `__ef_migrations`.
- Do not switch off readiness checks or point services at an empty database to "get green"; evaluations would then run with no snapshot or history.
- Do not return ALLOW from a wrapper when Axiom is down.
