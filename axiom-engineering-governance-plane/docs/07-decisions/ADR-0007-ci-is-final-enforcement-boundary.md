# ADR-0007 — CI Is the Final Enforcement Boundary

**Status:** Accepted  
**Date:** 2026-10-03

## Context
Agent hooks and instructions improve feedback but can be skipped, misconfigured, or manipulated.

## Decision
Protected merges are evaluated independently in CI against immutable commit SHA. Local/harness results are advisory acceleration, not final trust.

## Consequences
- Governance does not depend on model obedience.
- CI availability matters for merge flow.
- Receipts are generated for exact commit state.
