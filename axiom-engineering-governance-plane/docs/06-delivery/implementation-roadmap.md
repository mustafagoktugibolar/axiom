# Implementation Roadmap

## Stage A — Foundation
Outcome: trusted source and deterministic context.

Deliver:
- governance repo/schema;
- ownership/lifecycle;
- system/repo/component mapping;
- resolver;
- search;
- preflight API/MCP;
- audit evaluation IDs.

Exit criteria:
- pilot teams agree that resolved decisions are relevant;
- rebuild from Git works;
- no semantic model is required for correctness.

## Stage B — Executable governance
Outcome: repeatable violations become cheap checks.

Deliver:
- built-in policy engine;
- diff evaluation;
- CI status;
- receipts;
- exceptions;
- significant-change classifier;
- design template.

Exit:
- protected pilot PRs run Axiom;
- rules start in observe/warn.

## Stage C — Agent-native workflows
Outcome: governance appears before code, not only in CI.

Deliver:
- Kiro adapter/hooks;
- second harness adapter;
- design validation;
- agent bootstrap;
- portal review queue.

Exit:
- significant pilot changes generate design + receipt automatically.

## Stage D — Semantic intelligence
Outcome: detect conflicts not expressible as lint rules.

Deliver:
- bounded semantic analyzer;
- candidate decision extraction;
- conflict detection;
- duplicate-solution finding;
- reviewer feedback calibration.

Exit:
- measured false-positive rate within agreed threshold.

## Stage E — Organization rollout
Outcome: multi-repo standard.

Deliver:
- catalog/Backstage adapter;
- governance health;
- broader harness packs;
- metrics;
- training/operating model.

## Stage F — Advanced closure
Potential future:
- edit-scope tokens;
- cryptographic attestation;
- richer rule DSL;
- automatic proof obligations;
- policy packs;
- federated governance across business units.
