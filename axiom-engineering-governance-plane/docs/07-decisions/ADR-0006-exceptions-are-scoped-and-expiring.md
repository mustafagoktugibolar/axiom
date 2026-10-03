# ADR-0006 — Exceptions Are Scoped and Expiring

**Status:** Accepted  
**Date:** 2026-10-03

## Context
Permanent or broad waivers silently destroy standards.

## Decision
Exceptions target named governance records, have exact scope, require approval, and expire by default. Non-exemptable controls cannot be waived through the normal exception mechanism.

## Consequences
- Temporary debt is visible.
- Expiry can be monitored.
- Renewals become explicit architecture signals.
