# System Graph

## Objective

Give governance evaluations a reliable map of what a change can affect.

## Canonical entity kinds

- Organization
- Domain
- System
- Component
- Repository
- API
- Resource
- Database
- Queue
- EventStream
- DataProduct
- Deployment
- Capability
- Team
- Environment

## Canonical relations

| Relation | Example |
|---|---|
| CONTAINS | Domain contains System |
| OWNS | Team owns Component |
| IMPLEMENTS | Repository implements Component |
| PROVIDES | Component provides API |
| CONSUMES | Component consumes API |
| DEPENDS_ON | Component depends on Component |
| STORES_IN | Component stores in Database |
| PUBLISHES_TO | Component publishes to Stream |
| SUBSCRIBES_TO | Component subscribes to Stream |
| DEPLOYED_AS | Component deployed as Deployment |
| SUPPORTS | Component supports Capability |

## Provenance

Every node/edge has:
- source type;
- source locator;
- first seen;
- last seen;
- confidence;
- confirmed flag.

Sources:
1. Backstage/catalog — high trust when organization declares it authoritative.
2. Repository manifests — high trust.
3. Deployment/config discovery — medium/high.
4. Code/import inference — candidate unless confirmed.
5. LLM inference — candidate only.

## Impact analysis

The graph answers:
- who consumes this API?
- which repos implement affected components?
- which owners must review?
- which resources are shared?
- which components are downstream of this event contract?

Traversal is relation-specific. Avoid generic unbounded graph walks.

Example:

```text
API change
→ PROVIDES^-1 Component
→ CONSUMES^-1 Consumer Components
→ IMPLEMENTS^-1 Repositories
→ OWNS^-1 Teams
```

## Backstage integration

Backstage is optional. If present:
- Components, APIs, Resources, Systems and Domains map naturally.
- Backstage remains source for catalog-owned fields.
- Axiom adds governance-specific edges and evaluation data.
- Axiom must not require duplicating the full catalog in Git.

## Graph quality

Expose metrics:
- repos without component binding;
- components without owners;
- APIs with no provider;
- consumed APIs with no known provider;
- stale entities;
- inferred but unconfirmed high-impact edges.
