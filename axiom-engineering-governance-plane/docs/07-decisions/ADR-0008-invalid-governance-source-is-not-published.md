# ADR-0008 — An Invalid Governance Source Is Not Published

**Status:** Accepted  
**Date:** 2026-10-03

## Context
The projection worker turns a governance Git commit into a published snapshot. A commit can contain records that fail schema, lifecycle, ownership, relationship or exception validation. The specification does not say what the runtime does with such a commit. Publishing only the valid records would silently drop an accepted control whose file was broken by an edit, which turns a typo into less governance.

## Decision
A source commit is published as a snapshot only if it has no validation errors. If it has any, the synchronizer rejects the whole commit, records the issues on the sync checkpoint, and the last published snapshot stays in force. Warnings do not prevent publication. The same validation is available to governance repositories as a CI check (`axiom-cli governance validate`) so that invalid records are stopped before merge.

## Consequences
- A broken edit can never reduce governance; it can only delay new governance.
- Evaluations keep reporting the snapshot they used and its age, so staleness is visible (design.md §16).
- A rejected commit raises the stale-snapshot alert until it is fixed.
- Lifecycle transitions are validated between the last published revision and the new one, so an illegal transition is also a rejection.
