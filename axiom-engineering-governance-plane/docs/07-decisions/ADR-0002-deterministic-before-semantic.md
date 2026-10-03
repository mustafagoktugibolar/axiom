# ADR-0002 — Deterministic Enforcement Before Semantic Judgment

**Status:** Accepted  
**Date:** 2026-10-03

## Context
Many governance violations are mechanically checkable. LLM-based checks cost more, vary, and can create false positives.

## Decision
If a governance rule can be expressed deterministically with acceptable maintainability, enforce it deterministically. Semantic analysis is reserved for intent/rationale ambiguity and candidate discovery.

## Consequences
- More predictable blocking.
- Lower inference cost.
- Better replay/reproducibility.
- Rule authoring becomes a first-class engineering activity.
