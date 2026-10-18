# Schema or migration readiness failure

Migrations run in the init container `migrate` of every API pod (the API image with `--migrate`); EF's migration lock serializes replicas. `/health/ready` runs the `postgresql` check,
which is healthy only when the schema is fully migrated. Workers have the same gate (`ISchemaGate`) and wait quietly until it passes.

## Symptoms

- API pods stuck `Init:Error`, `Init:CrashLoopBackOff` or `Running` but not Ready; rollout hangs.
- `GET /health/ready` unhealthy: "The database is unreachable or its schema is not fully migrated (run the migration job)."
- Workers log Information `Database schema is not ready (migrations pending or database unreachable); ... waits.` for governance sync, health, catalog sync and event dispatch for longer than a rollout.
- No dedicated alert exists; latency/receipt alerts fire only if an older version is still serving.

## Diagnosis

1. `kubectl -n axiom get pods -l app.kubernetes.io/component=api` and `kubectl -n axiom logs <api-pod> -c migrate`.
2. Migration failed: the log shows the Npgsql/EF error (permissions to create objects in schema `axiom`, an existing object, lock timeout, connection failure).
   The database identity needs DDL rights for the migration run (see database owner); the runtime may use the same connection string, so check the Secret key `connection-string`.
3. Migration succeeded but readiness red: the image is older than the database (rolled back after a newer migration) or newer than the database (migration init container skipped). Compare image tags of `axiom-api` and `axiom-workers`
   with `kubectl -n axiom get deploy -o wide`.
4. Pending list: connect with `psql` and `SELECT "MigrationId" FROM axiom.__ef_migrations ORDER BY 1 DESC LIMIT 5;` (read only) and compare with the migrations in the deployed image's version.
5. Database unreachable: database-unavailable.md.

## Mitigation

- Fix the cause (grant, connection, lock holder: `SELECT pid, state, left(query,80) FROM pg_stat_activity WHERE query ILIKE '%migrat%';`), then delete the failing pod so the init container runs again:
  `kubectl -n axiom delete pod <api-pod>`.
- Version skew: deploy matching `axiom-api` and `axiom-workers` images (roll forward). EF migrations are additive in normal use, so rolling the API back below the database version is the case to avoid.
- Workers need no action; they resume on the next tick after readiness is green.

## Escalate

Escalate to the platform owner for any failed migration that left partial changes, or when a roll-forward is not possible. Provide the `migrate` container log and the last five rows of the history table.

## Do not

- Do not edit migrations, the model snapshot or `__ef_migrations` to make the check pass; do not apply DDL by hand to match the model.
- Do not bypass the readiness check or start the API with an unmigrated schema.
- Do not run `--migrate` from a laptop against production; use the deployment's migration step.
