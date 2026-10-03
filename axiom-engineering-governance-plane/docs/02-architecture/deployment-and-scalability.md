# Deployment and Scalability

## Reference deployment

```text
Kubernetes
  axiom-api (N replicas)
  axiom-worker (N replicas)
  axiom-policy-runner (isolated jobs/pods)
  axiom-portal
  PostgreSQL HA
  optional Redis
```

## Horizontal scaling

API/MCP hosts are stateless.
Workers partition by event/work item.
Policy jobs are independently scalable.
Semantic analysis uses concurrency/rate limits per provider/organization.

## PostgreSQL

Use:
- normalized governance projections;
- recursive CTEs for bounded graph traversal;
- GIN full text;
- pgvector for bounded semantic retrieval;
- partition evaluation/receipt tables if volume requires.

Do not introduce a graph DB until measured query complexity/latency warrants it.

## Consistency

Governance Git merge creates a new source snapshot.
Runtime projection is eventually consistent with a published snapshot marker.

Evaluation must record which snapshot it used. If projection is behind latest Git:
- surface freshness;
- optionally block high-risk gates if max staleness exceeded.

## Disaster recovery

Authoritative governance is recoverable from Git.
System graph recoverability depends on source adapters/manifests.
Receipts/evaluation history require database backup/WORM policy.

Recovery drills:
- rebuild fresh DB from governance repo;
- restore graph;
- replay catalog events;
- restore receipts;
- verify known receipt digest.

## Capacity baseline

Design for:
- 1k repos;
- 25k current+historical governance records;
- 10k software entities;
- 10k evaluations/day;
- 100 active agent sessions.

Scale testing should include bursty PR/agent traffic at workday peaks.
