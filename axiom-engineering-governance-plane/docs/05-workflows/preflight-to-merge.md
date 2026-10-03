# Workflow — Preflight to Merge

## Standard significant change

```mermaid
sequenceDiagram
    participant Dev
    participant Agent
    participant Axiom
    participant Owner
    participant CI

    Dev->>Agent: Request change
    Agent->>Axiom: Preflight
    Axiom-->>Agent: Applicable decisions + significant=true
    Agent->>Agent: Produce design
    Agent->>Axiom: Validate design
    alt conflict requires authority
      Axiom-->>Agent: REQUIRE_REVIEW
      Agent-->>Dev: Stop and surface conflict
      Dev->>Owner: Review
      Owner->>Axiom: Approve decision/exception/design outcome
    else design valid
      Axiom-->>Agent: ALLOW
    end
    Agent->>Agent: Implement
    Agent->>Axiom: Validate diff
    Axiom-->>Agent: Diff verdict
    Dev->>CI: Open/update PR
    CI->>Axiom: Revalidate immutable SHA
    Axiom-->>CI: Verdict + receipt
    CI-->>Dev: Merge status
```

## Rules

- Agent must not translate `REQUIRE_REVIEW` into “probably okay”.
- Design approval applies to a bounded scope and design hash.
- Actual diff can invalidate approval.
- CI uses commit SHA, not working-tree claims.
- The receipt is for the exact governed state and code revision.

## Trivial change fast path

Low-risk changes can:
- preflight automatically;
- skip formal design;
- run local/CI deterministic checks;
- merge if no governance escalation occurs.

Examples:
- typo/docs;
- local test-only change;
- implementation inside existing bounded pattern with no public contract/topology/security change.

The significant-change classifier is policy-driven.
