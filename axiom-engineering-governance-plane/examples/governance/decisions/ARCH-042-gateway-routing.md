---
schemaVersion: axiom.io/v1
kind: Decision
metadata:
  id: ARCH-042
  title: Gateway must not contain business routing logic
  owners: [platform-architecture]
  tags: [gateway, routing]
spec:
  status: accepted
  scope:
    systems: [gui-platform]
    components: [api-gateway]
    repositories: [gateway]
    capabilities: [routing, homepage-resolution]
  authority:
    level: system
    exemptable: true
  enforcement:
    defaultVerdict: require_review
    rules:
      - ruleId: gateway-no-domain-db-access
        mode: block
  decision: >
    The API Gateway may perform transport-level routing and authentication
    handoff, but role/user/domain-specific business routing belongs in a
    domain service.
  forbidden:
    - Resolve homepage by querying domain state directly from the Gateway.
    - Add domain database access to the Gateway for routing decisions.
  preferred:
    - Call Homepage Service through its supported contract.
  relationships:
    related: [SYS-GUI-001]
  validity:
    effectiveFrom: 2026-09-01
    reviewAfter: 2027-03-01
---

## Context

The gateway is shared infrastructure. Business-specific resolution in the gateway couples transport concerns to domain rules and creates an ownership bottleneck.

## Consequences

- Gateway remains stateless with respect to domain routing.
- Homepage Service owns role/person-specific homepage resolution.
- New business routing needs a domain-owned service/capability.
