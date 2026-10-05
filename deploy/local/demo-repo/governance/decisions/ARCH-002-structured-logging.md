---
schemaVersion: axiom.io/v1
kind: Decision
metadata:
  id: ARCH-002
  title: Services log structured JSON with a correlation ID
  owners: [platform-architecture]
  tags: [observability, logging]
spec:
  status: accepted
  scope:
    systems: [gui-platform]
  authority:
    level: system
    exemptable: true
  enforcement:
    defaultVerdict: warn
  decision: >
    Every service in the GUI platform writes structured JSON logs and propagates the
    correlation ID received on the incoming request.
  preferred:
    - Use the shared logging package instead of ad-hoc console output.
  validity:
    effectiveFrom: 2026-01-01
    reviewAfter: 2027-06-01
---

## Context

Free-text logs cannot be correlated across the Gateway and the domain services.
