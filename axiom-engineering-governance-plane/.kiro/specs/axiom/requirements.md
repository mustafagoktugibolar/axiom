# Axiom Requirements

## 1. Introduction

Axiom is a vendor-neutral engineering governance plane that provides authoritative architecture context and enforceable constraints to humans, AI coding agents, CI/CD systems, and developer portals across many repositories.

This document uses EARS-style language where practical.

## 2. Definitions

- **Governance Record:** principle, standard, decision, system goal, exception, or executable invariant.
- **Authoritative Record:** human-approved, active governance record.
- **Candidate Record:** AI- or human-proposed record that does not govern until approved.
- **System Graph:** topology of domains, systems, components, repositories, APIs, resources, ownership, and dependencies.
- **Significant Change:** a change matching organization-defined risk/impact thresholds.
- **Receipt:** immutable evaluation evidence tied to a governance snapshot.
- **Harness:** AI coding environment/agent such as Kiro, Claude Code, Codex, Cursor, or Copilot.
- **Preflight:** evaluation performed before design or editing.
- **Deterministic Rule:** machine-checkable policy whose result does not depend on probabilistic model judgment.
- **Semantic Finding:** LLM-assisted finding with cited governance evidence and confidence.

## 3. Functional requirements

### R1 — Governance source of truth
WHEN a governance record is created or modified, THE System SHALL persist the authoritative form in a version-controlled Git repository.

Acceptance criteria:
- Every record has stable ID, schema version, lifecycle status, owners, scope, and revision history.
- Runtime projections can be deleted and fully rebuilt from authoritative sources.
- A database-only change cannot make a record authoritative.

### R2 — Decision lifecycle
THE System SHALL support `proposed`, `accepted`, `deprecated`, `superseded`, and `rejected` lifecycle states.

Acceptance criteria:
- Only `accepted` records govern by default.
- Superseded/deprecated records remain searchable as historical evidence.
- Transition to `accepted` requires an authorized human approval path.

### R3 — Scoped governance
WHEN evaluating a task, design, file set, diff, or PR, THE System SHALL resolve governance by explicit scope before semantic similarity.

Scope dimensions SHALL support:
- organization/domain/system/component/repository;
- path/file glob;
- capability;
- API/resource/queue/database;
- language/framework/technology;
- environment;
- change/risk class.

### R4 — Software topology
THE System SHALL maintain or consume a system graph containing components, repositories, APIs, resources, dependencies, and ownership.

Acceptance criteria:
- Impact analysis can traverse upstream and downstream dependencies.
- Repository-to-component mapping is explicit.
- Missing topology is surfaced as a quality gap, not silently inferred as fact.

### R5 — Preflight
WHEN an AI harness starts a significant change, THE System SHALL provide a preflight evaluation before implementation.

The response SHALL include:
- resolved affected scope;
- applicable authoritative records;
- active exceptions;
- known conflicts;
- required design/review steps;
- required deterministic checks;
- affected dependencies;
- governance snapshot ID;
- evaluation verdict.

### R6 — Design gate
WHEN a significant change requires a design, THE System SHALL validate the design against applicable governance before implementation is authorized.

Acceptance criteria:
- Design declares goal, non-goals, affected systems, decisions consulted, data/control flows, failure modes, security, observability, rollout, and migration where relevant.
- Missing mandatory design sections produce actionable findings.
- Conflicts reference exact governance IDs.

### R7 — Diff validation
WHEN implementation produces a diff, THE System SHALL evaluate the actual changed files independently of the original plan.

Acceptance criteria:
- Newly affected scope is detected.
- Deterministic rules run on the actual diff/repository state.
- Semantic analysis compares intent/design to implementation when configured.
- Scope expansion may invalidate the earlier design approval.

### R8 — CI/PR enforcement
WHEN a protected PR is evaluated, THE System SHALL be able to enforce governance independently of the coding harness.

Acceptance criteria:
- CI never trusts a local “pass” without revalidation.
- Blocking results map to non-zero pipeline status.
- Required human review can be expressed separately from deterministic failure.

### R9 — Enforcement levels
THE System SHALL support `INFO`, `WARN`, `REQUIRE_REVIEW`, and `BLOCK`.

Rules:
- Deterministic violations may `BLOCK`.
- Missing mandatory preconditions may `BLOCK`.
- Semantic findings default to `WARN` or `REQUIRE_REVIEW`.
- Direct semantic `BLOCK` requires explicit organization policy and named accountable owner.

### R10 — Exceptions
WHEN a deviation is necessary, authorized users SHALL be able to create a scoped exception.

An exception SHALL include:
- target governance IDs;
- exact scope;
- rationale;
- owner;
- approver;
- start and expiration;
- compensating controls where required.

Expired exceptions SHALL cease to apply automatically.

### R11 — Non-exemptable controls
THE System SHALL support governance rules marked `exemptable: false`.

A normal exception SHALL NOT override those rules.

### R12 — Conflict handling
WHEN two authoritative applicable records conflict and precedence cannot resolve them, THE System SHALL fail closed into explicit human review.

The System SHALL NOT choose one using embedding similarity or LLM preference.

### R13 — MCP
THE System SHALL expose vendor-neutral MCP tools for preflight, context retrieval, decision query, impact analysis, design validation, diff validation, and receipt retrieval.

### R14 — Harness adapters
THE System SHALL support thin harness-specific integrations without duplicating governance content.

Initial adapters SHOULD include:
- Kiro
- Claude Code
- OpenAI Codex
- Cursor
- GitHub Copilot

### R15 — Agent bootstrap
Each integrated repository SHALL be able to include a small bootstrap instruction that requires Axiom preflight and post-change validation.

The bootstrap SHALL NOT copy the entire governance corpus into the repository.

### R16 — Audit receipts
AFTER every material evaluation, THE System SHALL be able to emit an immutable receipt containing:
- actor and harness identity;
- repository/commit/PR identifiers;
- task/design identifiers;
- governance snapshot hash;
- exact applied record revisions;
- rule versions;
- findings and verdict;
- timestamps.

### R17 — Knowledge onboarding
WHEN an existing organization adopts Axiom, THE System SHOULD assist in discovering candidate decisions from code, docs, PRs, incidents, and repeated patterns.

Generated candidates SHALL remain non-authoritative until approved.

### R18 — Learning loop
WHEN review or incident analysis identifies a repeatable failure mode, THE System SHALL support converting that lesson into:
- a governance record;
- a deterministic rule when possible;
- a semantic review instruction otherwise;
- a regression test/evaluation.

### R19 — Search and explainability
Users SHALL be able to search governance by ID, text, scope, system, owner, status, technology, and relationship.

Every finding SHALL answer:
- what applies;
- why it applies;
- what evidence was used;
- what action resolves it.

### R20 — Ownership
Every authoritative governance record SHALL have an accountable owner.

Stale/orphaned records SHALL be detectable.

### R21 — Freshness
THE System SHALL support review dates and freshness policies.

A stale record SHALL NOT silently disappear from governance; it SHALL be flagged to owners.

### R22 — Authorization
THE System SHALL enforce enterprise authentication and role/scope-aware authorization.

Roles SHALL distinguish at least:
- Reader
- Contributor
- Decision Owner
- Approver
- Exception Approver
- Platform Admin
- Auditor

### R23 — Multi-tenancy / organizational boundaries
IF Axiom serves multiple organizational boundaries, THE System SHALL prevent governance and topology leakage across tenants/organizations unless explicitly shared.

### R24 — Availability degradation
IF semantic analysis or vector retrieval is unavailable, deterministic resolution and hard-policy enforcement SHALL continue where possible.

The system SHALL report degraded semantic coverage.

### R25 — Rebuildability
WHEN runtime indexes are lost, THE System SHALL rebuild them from Git and configured catalog sources without loss of authoritative governance history.

## 4. Non-functional requirements

### Performance
- p95 deterministic preflight: <1.5 s at normal load.
- p95 preflight with semantic analysis: <5 s.
- p95 governance query: <500 ms.
- p95 ordinary PR deterministic evaluation: <30 s.
- First UI contentful governance search result: <2 s under normal network conditions.

### Scale baseline
Initial reference target:
- 1,000 repositories
- 10,000 components/resources/APIs
- 25,000 governance records including history
- 5,000 developers
- 100 concurrent agent sessions
- 10,000 evaluations/day

Architecture SHALL scale horizontally beyond this without changing public contracts.

### Reliability
- Read/preflight path availability target: 99.9% monthly.
- No accepted governance revision may be lost due to runtime DB loss.
- Duplicate webhook/event delivery must be safe.

### Security
- All write operations authenticated and authorized.
- Secrets never stored in governance records.
- Custom rules execute with least privilege.
- Audit trail is append-oriented and tamper-evident.

### Privacy
- Semantic provider requests SHOULD minimize source content.
- Sensitive source snippets SHOULD remain local/private where organization policy requires.
- Provider/model choice SHALL be configurable per organization.

## 5. Core correctness properties

P1. **Authority property:** non-accepted records cannot govern.
P2. **Rebuild property:** the same authoritative source snapshot produces equivalent governance projections.
P3. **Determinism property:** deterministic rules produce the same result for identical inputs and rule versions.
P4. **Conflict property:** unresolved same-authority conflicts cannot result in silent `ALLOW`.
P5. **Exception property:** an exception only affects its declared scope and validity window.
P6. **Non-exemptable property:** exceptions never disable non-exemptable hard rules.
P7. **Receipt property:** every merge-gating verdict can be traced to exact source revisions and rule versions.
P8. **Scope monotonicity property:** when actual changed scope expands, prior governance coverage cannot be assumed sufficient.
P9. **Semantic safety property:** model uncertainty cannot convert an authoritative deterministic failure into success.
P10. **Historical property:** superseding a decision preserves old revisions for audit and old-revision receipt replay.
