# Domain Model

## Governance record

Canonical logical fields:

```yaml
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
    paths: []
    capabilities: [routing, homepage-resolution]
    technologies: []
    environments: []
  authority:
    level: architecture
    exemptable: true
  enforcement:
    defaultVerdict: require_review
    rules:
      - ruleId: gateway-no-business-routing
        mode: block
  decision: >
    The gateway may perform transport-level routing but must not implement
    user/role/domain-specific business routing.
  forbidden:
    - Role-based homepage resolution inside the gateway
    - Direct domain database lookup for homepage routing
  preferred:
    - Delegate homepage resolution to Homepage Service
  relationships:
    supersedes: []
    supersededBy: []
    refines: []
    related: [SYS-GUI-001]
    conflictsWith: []
  validity:
    effectiveFrom: 2026-09-01
    reviewAfter: 2027-03-01
```

Markdown rationale may accompany the YAML frontmatter/body.

## Record kinds

### Principle
Broad direction; normally advisory or review-level.

### Standard
Repeatable organization/team requirement.

### Decision
Context-specific architectural choice.

### Goal
Desired system property, strategic target, or migration direction.

### Exception
Scoped deviation from exemptable records.

### Candidate
Candidate is a lifecycle/authority property, not a separate kind.

## Scope

A scope is a conjunction across specified dimensions and wildcard across omitted dimensions.

Supported dimensions:
- organization
- domain
- system
- component
- repository
- path
- capability
- API/resource
- technology
- environment
- change class

Example:

```yaml
scope:
  systems: [payments]
  repositories: [payments-api]
  paths: ["src/Payments.Infrastructure/**"]
  technologies: ["postgresql"]
```

means the record applies only when the evaluation intersects that bounded scope.

## Authority

Suggested authority levels:

```text
regulatory/security-hard
organization
domain
system
component
repository
advisory
```

Authority is not the same as specificity. A repository-specific rule cannot weaken a non-exemptable organization security rule.

## Relations

- `supersedes`
- `supersededBy`
- `refines`
- `implements`
- `conflictsWith`
- `related`
- `motivatedBy` (incident, issue, review)
- `requires`
- `exceptionTo`

Relations must use stable IDs.

## Evaluation

```text
Evaluation
  id
  stage
  actor
  harness
  repository + ref
  task
  designRef?
  diffRef?
  governanceSnapshot
  resolvedScope[]
  appliedRecords[]
  appliedExceptions[]
  policyRuns[]
  semanticFindings[]
  verdict
  resolutionTrace
```

## Receipt

A receipt is a finalized evaluation summary tied to immutable source coordinates.

It SHOULD be content-addressed:

```text
receiptDigest =
SHA256(
  normalized request +
  commit SHA +
  governance snapshot +
  applied record revisions +
  policy versions +
  findings +
  verdict
)
```

## Software graph entity

```yaml
kind: Component
metadata:
  id: component:gui/api-gateway
  name: api-gateway
spec:
  system: system:gui-platform
  owner: team:platform
  repositories:
    - repo:gateway
  providesApis: []
  consumesApis:
    - api:homepage-service/v1
  dependsOn:
    - component:auth/auth-service
```

The graph may ingest Backstage, repo metadata, service manifests, and controlled discovery. Inferred relations must be labeled with provenance/confidence until confirmed.
