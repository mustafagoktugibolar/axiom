# Package Manifest

This ZIP is intended to be usable as:

1. A product proposal.
2. A Kiro-style product Spec.
3. A system architecture baseline.
4. An implementation handoff.
5. A governance-model starter kit.
6. A seed repository for a real Axiom implementation.

## Canonical documents

| Concern | Canonical document |
|---|---|
| Product purpose | `.kiro/steering/product.md` |
| Product requirements | `.kiro/specs/axiom/requirements.md` |
| Technical design | `.kiro/specs/axiom/design.md` |
| Implementation plan | `.kiro/specs/axiom/tasks.md` |
| Governance data model | `docs/02-architecture/domain-model.md` |
| Decision resolution | `docs/02-architecture/decision-resolution-engine.md` |
| MCP interface | `docs/04-contracts/mcp-contract.md` |
| HTTP interface | `docs/04-contracts/rest-api.yaml` |
| Enforcement semantics | `docs/03-governance/enforcement-model.md` |
| Delivery roadmap | `docs/06-delivery/implementation-roadmap.md` |
| External research | `references/research-sources.md` |

If two documents disagree, the Kiro Spec (`requirements.md` + `design.md`) wins until an ADR explicitly supersedes it.
