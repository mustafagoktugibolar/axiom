# Vision and Strategy

## Vision

Make every significant software change aware of the organization's engineering intent before it becomes code.

Axiom should make “Why are we doing it this way?” as machine-accessible as “What does this function do?”

## Strategic thesis

AI coding agents compress implementation time, but architecture governance still runs at human speed. Without a shared governance plane, throughput amplifies inconsistency. The answer is not to slow agents down with larger prompt manuals. The answer is to create a durable control plane where accepted decisions are versioned, scoped, queryable, and increasingly executable.

## Differentiation

Axiom is not only:
- ADR storage;
- RAG over docs;
- code linting;
- a developer portal;
- an MCP server;
- a PR review bot.

Its differentiated value is the **closed loop**:

```text
Decision
→ agent context
→ design constraint
→ deterministic/semantic verification
→ PR evidence
→ review/incident learning
→ improved decision/rule
```

## Strategic product layers

### Layer 1 — Authority
Human-approved decisions and ownership.

### Layer 2 — Topology
The map of software and dependencies.

### Layer 3 — Resolution
Which authority applies here?

### Layer 4 — Enforcement
What is mechanically provable?

### Layer 5 — Intelligence
What semantic contradiction or emerging decision is visible?

### Layer 6 — Learning
How does a review/incident become permanent organizational knowledge?

## North-star outcome

A developer can ask any AI coding harness to make a change, and the harness automatically behaves like an engineer who:
- knows the relevant architecture history;
- knows which neighboring systems will be affected;
- knows what it is forbidden to change;
- knows which decisions are current;
- knows when it is about to create a new architecture decision;
- knows when to stop and ask for human authority.

## Build vs buy philosophy

Integrate existing strengths:
- Backstage for organizations that already have a software catalog.
- SCMs for source/PR workflows.
- Semgrep/Roslyn/architecture tests/OPA-like engines where appropriate.
- Existing AI harnesses through MCP/hooks.

Build the unique layer:
- governance data model;
- authority/scope resolver;
- cross-repo decision context;
- evidence/receipt model;
- vendor-neutral workflow;
- conflict and exception semantics.
