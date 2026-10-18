# Axiom runbooks (plan item 12.10)

One runbook per scenario the code and the shipped alerts support. Every metric, alert, endpoint, log message
and column named here was checked against the code (`Telemetry.cs`, `deploy/observability/axiom-alerts.yaml`,
`src/Axiom.Api/Endpoints`, `src/Axiom.Workers`).

| Scenario | Runbook |
|---|---|
| Governance sync stuck or failing | [governance-sync.md](governance-sync.md) |
| Stale governance snapshot | [stale-snapshot.md](stale-snapshot.md) |
| Outbox lag, dead-lettered events | [outbox-and-dead-letters.md](outbox-and-dead-letters.md) |
| Receipt chain verification failure | [receipt-chain.md](receipt-chain.md) |
| LLM provider degraded | [llm-provider-degraded.md](llm-provider-degraded.md) |
| SCM fetch or status failure | [scm-failure.md](scm-failure.md) |
| Database unavailable | [database-unavailable.md](database-unavailable.md) |
| Exception expiry wave | [exception-expiry.md](exception-expiry.md) |
| Schema or migration readiness failure | [schema-readiness.md](schema-readiness.md) |
| Secret rotation | [secret-rotation.md](secret-rotation.md) |
| Custom runner incident | [custom-runner-incident.md](custom-runner-incident.md) |

## Conventions

- Examples use the Kustomize names: namespace `axiom`, Deployments `axiom-api` and `axiom-workers`, Secret `axiom-secrets`,
  ConfigMap `axiom-config`. With Helm the object names carry the release prefix; list them with `kubectl -n axiom get deploy,secret`.
- API calls need a bearer token whose `org` claim is the affected organization: `curl -H "Authorization: Bearer $TOKEN" https://<host>/...`.
  Every query is scoped to the token's organization, so repeat per organization.
- `psql` examples use the `axiom` schema (`axiom.outbox_event`, `axiom.receipt`, ...). Open a session with the connection string from
  Secret key `connection-string`. Prefer read-only statements; every write below is called out.
- Metric names in Prometheus form follow the translation described in `axiom-alerts.yaml` (`.` becomes `_`, counters gain `_total`).
- Alerts that exist today: `AxiomPreflightLatencySloBreach`, `AxiomPullRequestLatencySloBreach`,
  `AxiomSemanticPreflightLatencySloBreach`, `AxiomReceiptGenerationFailing`, `AxiomScmStatusPublishingFailing`,
  `AxiomGovernanceProjectionLag`, `AxiomUnresolvedAuthorityConflicts`, `AxiomBlockRateHigh`, `AxiomExceptionsInHeavyUse`,
  `AxiomSemanticFalsePositiveRateHigh`. There is no alert yet for the event outbox, stale snapshots, readiness or the
  custom runner; those runbooks start from the metrics, logs or probes named in them.

## Rules that hold in every runbook

1. A failed evaluation is never an ALLOW. Do not work around `GOVERNANCE_UNAVAILABLE` by returning a verdict from outside Axiom.
2. Never edit, delete, reorder or re-create rows of `axiom.receipt` or `axiom.evaluation` to make a check go green.
3. Git stays the authority for governance records. Do not insert or update `governance_*` tables by hand; fix the repository and let the sync publish.
4. Do not mark outbox events delivered by hand without confirming the sink received them.
5. Do not edit EF migrations or the `__ef_migrations` history table.
