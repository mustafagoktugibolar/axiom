# ADR-0004 — System Graph Uses PostgreSQL First

**Status:** Accepted  
**Date:** 2026-10-03

## Context
Axiom needs dependency traversal, but introducing a graph database adds operational complexity before query patterns are measured.

## Decision
Store typed entities/edges in PostgreSQL and use bounded recursive queries initially. Keep the domain contract graph-oriented so a specialized graph store can be introduced later if justified.

## Consequences
- Smaller operational footprint.
- Easier transactional integration with projections.
- Must benchmark high-degree/deep traversals.
