# Remaining work: dependency tree

Derived from `axiom-engineering-governance-plane/.kiro/specs/axiom/tasks.md` and verified against the code
on 2026-10-18 (not only against `project-status.md`). Done phases (1-6 except below, 8, 9.1-9.6, 10.1-10.7,
7.1-7.5/7.9 basic/7.12) are omitted.

## Legend

- **Needs** = must exist before the item can start. **Unblocks** = what waits for it.
- **Human** items are organizational decisions or external actions; the mechanism may be built, the decision is not.
- Every item that is not Human and not Environment-blocked belongs to a wave. A wave starts when its needs are merged.

## Tree

```text
Platform events
  A1  Outbox dispatcher worker (events are written, nothing sends them)      needs: -            unblocks: A2, B3
  A2  Webhook / log sinks, retry + dead-letter                               needs: A1

Governance health
  B1  Health jobs in Workers: stale / review-due records (1.11 job form)     needs: -            unblocks: B2, B3, 10.8
  B2  Exception-expiry review: exception.expiring events (13.7 mechanism)    needs: B1 (A1 only for delivery)
  B3  Health summary API                                                     needs: B1           unblocks: 10.8, 13.9
  10.8 Portal health dashboard                                               needs: B3 (wave 2)

Semantic (Phase 7, rest)
  7.6  Duplicate-solution analyzer                                            needs: 7.1-7.5 (done)
  7.7  "New architectural decision" detector                                  needs: 7.1-7.5 (done) unblocks: 7.8
  7.10 Persist reviewer overturn feedback                                     needs: ReviewService (done) unblocks: 7.11
  7.11 False-positive metrics (per code / provider / model)                   needs: 7.10         unblocks: 7.9 full calibration
  7.8  Candidate-ADR extraction                                               needs: 7.7, 11.5 (wave 2)

Knowledge onboarding (Phase 11)
  11.5 Candidate record model + store (candidates are never authoritative)   needs: -            unblocks: 11.1, 11.2, 11.3, 11.4, 11.6, 11.7, 7.8, governance.propose_decision
  11.8 Governance coverage by repository                                     needs: graph + records (done)   unblocks: 13.9
  11.1 Import existing ADRs as candidates                                     needs: 11.5 (same agent, sequential)
  11.2 Scan repo docs for candidate decisions                                 needs: 11.5 (wave 2)
  11.6 Human approval queue (API + portal)                                    needs: 11.5 (wave 2)
  11.7 Duplicate candidate clustering                                         needs: 11.5 (wave 2)
  11.3 Mine repeated PR review comments                                       needs: 11.5, SCM comment API (wave 3)
  11.4 Mine incidents / postmortems                                           needs: 11.5 (wave 3)
  MCP governance.propose_decision                                             needs: 11.5 (wave 2)

Policy engine
  5.7  Isolated custom-policy runner (Axiom.PolicyRunner process)             needs: -            unblocks: 5.8, 12.3
  5.8  Time / memory / network limits                                         needs: 5.7 (same agent, sequential)
  12.3 Sandbox escape testing                                                 needs: 5.7, 5.8 (wave 2)

Resolver / design
  6.7  Design vs. topology scope mismatch (depth via graph)                   needs: -
  3.10 Resolution benchmark at target scale                                   needs: -

Harness
  9.7-9.9 Codex / Cursor / Copilot packs                                      needs: -  (9.4 AGENTS.md done)  unblocks: 9.10
  9.10    Integration conformance tests                                       needs: 9.7-9.9 (same agent, sequential)

Portal
  10.3 Decision graph view                                                    needs: relations API
  10.8 Health dashboard                                                       needs: B3 (wave 2)
  10.10 Accessibility / security review                                       needs: all portal pages final (last wave)

Hardening (Phase 12)
  12.1 Threat-model review vs. current code                                   needs: -
  12.5 Chaos tests: LLM / vector / Git / DB degradation (fakes)               needs: 7.x done for LLM part
  12.8 Receipt replay / reproducibility test                                  needs: -
  12.9 SLO dashboards and alerts (deploy artifacts)                           needs: telemetry (done)
  12.10 Runbooks                                                              needs: 12.1, 12.5, 12.9, B2 (last wave)
  12.7 Projection rebuild drill, 12.6 Backup / restore, 12.4 Load test        ENVIRONMENT-BLOCKED: need Docker + a running stack
```

## Not doable by code (Human or deferred)

| Item | Why |
|---|---|
| 0.1-0.5, 0.8 | Ratification, owners, branch protection, pilot repositories are organizational decisions |
| 12.2 | Penetration test needs an independent tester |
| 13.1-13.6, 13.8, 13.10 | Rollout, training, reviews: organizational |
| 13.9 | Scorecard needs real adoption data; the inputs (B3, 11.8) are built |
| External Secrets Operator | Deferred by decision (see `project-status.md`, Planned later) |

## Waves

| Wave | Items (parallel) | Starts when |
|---|---|---|
| 1 | A1+A2, B1+B2+B3, 7.6+7.7, 7.10+7.11, 11.5+11.8+11.1, 5.7+5.8, 6.7+3.10, 9.7-9.10, 10.3, 12.1+12.5+12.8+12.9 | now |
| 2 | 11.2, 11.6, 11.7, propose_decision, 7.8, 10.8, 12.3 | wave 1 merged |
| 3 | 11.3, 11.4, 12.10, 10.10 | wave 2 merged |

Persistence note: the schema is a single `Initial` migration (not deployed). Parallel agents add entities and
DbContext configuration but do not generate migrations; one regeneration happens after each merge.
