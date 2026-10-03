# Observability and SLOs

## Service level objectives

| Capability | Target |
|---|---:|
| Read/preflight availability | 99.9% monthly |
| p95 deterministic preflight | <1.5 s |
| p95 semantic preflight | <5 s |
| p95 governance search | <500 ms |
| p95 standard PR deterministic evaluation | <30 s |
| Governance projection freshness after merge | <60 s target |

## Golden signals

- traffic by endpoint/tool/evaluation stage;
- latency;
- error rate;
- saturation;
- ingestion lag;
- policy runner queue;
- semantic provider latency/errors/cost.

## Governance-specific metrics

- verdict distribution;
- violations by decision;
- repeated reviewer overrides;
- semantic false-positive rate;
- exception count and expiry;
- stale/orphaned records;
- percentage of significant PRs with validated design;
- percentage of protected PRs with receipt;
- average applied context size;
- resolution conflicts;
- topology coverage.

## Trace correlation

Carry:
- `trace_id`;
- evaluation ID;
- repository ID;
- commit SHA;
- governance snapshot;
- harness type;
- organization ID.

Do not place sensitive task/source contents in metric labels.

## Alerts

- projection lag > threshold;
- receipt generation failures;
- policy runner failure spike;
- hard-rule evaluation unavailable;
- stale governance snapshot;
- exception expiring with active usage;
- cross-source catalog sync failures.
