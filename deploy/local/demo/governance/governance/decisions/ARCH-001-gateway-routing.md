---
schemaVersion: axiom.io/v1
kind: Decision
metadata:
  id: ARCH-001
  title: Gateway must not contain business routing logic
  owners: [platform-architecture]
  tags: [gateway, routing]
spec:
  status: accepted
  scope:
    systems: [gui-platform]
    components: [api-gateway]
    repositories: [demo-gateway]
  authority:
    level: system
    exemptable: true
  enforcement:
    defaultVerdict: require_review
    rules:
      - ruleId: forbidden-import
        mode: block
        with:
          patterns: "Npgsql"
          paths: "src/**"
  decision: >
    The API Gateway may perform transport-level routing and authentication handoff,
    but user- or domain-specific business routing belongs in a domain service.
  forbidden:
    - Resolve a user's landing page by querying domain state directly from the Gateway.
    - Add domain database access to the Gateway for routing decisions.
  preferred:
    - Call the owning domain service through its published contract.
  validity:
    effectiveFrom: 2026-01-01
    reviewAfter: 2027-06-01
---

## Context

Business routing in the Gateway couples it to domain data and makes every routing change a Gateway release.

## Consequences

Changes that add domain logic to the Gateway need an architecture review before they merge.
