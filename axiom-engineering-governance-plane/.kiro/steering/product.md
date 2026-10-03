# Product Steering — Axiom

## Product

Axiom is an **Engineering Governance Plane for AI-Assisted Software Development**.

It exists to ensure that humans and AI coding agents make changes consistent with accepted architecture decisions, system goals, engineering standards, security invariants, and known dependencies across many repositories.

## Problem

Organizations increasingly use multiple AI coding harnesses across many repositories. Coding throughput rises faster than shared architectural understanding. Important decisions are often trapped in senior engineers' memory, chat threads, old PRs, stale wiki pages, or inconsistent repository instruction files.

This causes:

- conflicting implementations across repositories;
- accidental reversal of previous architecture decisions;
- duplicated solutions;
- implicit architectural choices hidden inside agent-generated code;
- architecture review becoming an expensive bottleneck;
- inconsistent behavior between AI harnesses;
- stale instructions and context pollution;
- weak auditability of why a generated change was considered acceptable.

## Product promise

For every meaningful engineering change, Axiom can answer:

1. What systems/components/repositories are affected?
2. What accepted decisions, standards, invariants, goals, and exceptions apply?
3. Does the proposal conflict with any of them?
4. What is mechanically enforceable?
5. What requires semantic or human review?
6. What evidence must exist before the change may merge?
7. What changed in organizational knowledge as a result?

## Target users

- Software engineers using AI coding agents
- Staff/principal engineers
- Architects
- Platform engineering teams
- Security engineering
- Engineering managers
- Developer productivity teams
- AI platform teams
- CI/CD and internal developer platform owners

## Primary jobs-to-be-done

- **Before design:** discover relevant organizational constraints automatically.
- **During design:** detect conflicts and force explicit architectural reasoning.
- **During implementation:** keep agent edits inside approved scope and patterns.
- **At PR:** prove compliance independently of the agent.
- **After review/incidents:** convert lessons into durable decisions or executable rules.
- **Across repositories:** understand impact using a shared software topology.
- **During migration:** know which decisions are current, superseded, exceptional, or expiring.

## Non-goals

Axiom is not:

- a coding agent;
- a replacement for Git, CI, Backstage, or issue tracking;
- a general company wiki;
- a magical architecture oracle that invents authority from code;
- a vector database with a chat UI;
- a policy engine that blindly blocks all deviations;
- a system where LLM output alone can create authoritative rules.

## Success metrics

### Product
- ≥90% of significant AI-assisted PRs contain a governance receipt.
- ≥95% of hard-policy violations are caught before merge.
- ≥80% of developers report that relevant architecture context appears without manual searching.
- ≥50% reduction in repeated architecture-review comments after six months.
- <5% false-block rate for deterministic checks.
- <15% false-positive rate for semantic “requires review” findings after tuning.

### Operational
- p95 preflight latency <1.5 s without semantic analysis; <5 s with semantic analysis.
- p95 deterministic diff validation <30 s for normal PRs.
- 99.9% monthly availability for read/preflight paths.
- Governance Git source can be rebuilt into runtime projections from zero.
