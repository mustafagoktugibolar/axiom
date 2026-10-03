# ADR-0005 — Semantic Findings Do Not Block by Default

**Status:** Accepted  
**Date:** 2026-10-03

## Context
Semantic architecture review is useful but probabilistic.

## Decision
Semantic findings default to INFO/WARN/REQUIRE_REVIEW. Direct BLOCK is disabled unless an organization explicitly opts into a named high-risk policy with accountable ownership.

## Consequences
- Lower risk of model-driven development outages.
- Human judgment remains the authority for ambiguous semantics.
