# ADR-0001 — Git Is the Authoritative Governance Store

**Status:** Accepted  
**Date:** 2026-10-03

## Context
Governance must be versioned, reviewable, attributable, branch-protected, and recoverable independently of runtime database state.

## Decision
Authoritative governance records live in a Git repository. Runtime databases are projections/indexes.

## Consequences
- Approval can reuse established PR/CODEOWNERS workflows.
- Every record has immutable commit provenance.
- Runtime DB can be rebuilt.
- Portal writes must ultimately create/merge Git changes.
- Evaluation receipts reference both Axiom snapshot and source commit.
