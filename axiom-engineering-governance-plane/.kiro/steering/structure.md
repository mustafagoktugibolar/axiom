# Repository Structure Steering — Axiom

```text
src/
  Axiom.Api/
  Axiom.Application/
  Axiom.Domain/
  Axiom.Infrastructure/
  Axiom.Mcp/
  Axiom.Workers/
  Axiom.PolicyRunner/
  Axiom.Portal/

tests/
  Axiom.UnitTests/
  Axiom.IntegrationTests/
  Axiom.ContractTests/
  Axiom.ArchitectureTests/
  Axiom.E2E/

governance/
  principles/
  standards/
  decisions/
  goals/
  exceptions/
  schemas/

docs/
  architecture/
  operations/
  runbooks/

deploy/
  helm/
  kustomize/

tools/
  axiom-cli/
```

## Structural constraints

- Domain contains no Infrastructure references.
- Application depends on Domain abstractions, not concrete external clients.
- Integrations live behind ports/adapters.
- MCP and REST invoke the same application use cases.
- Policy implementations never mutate governance state.
- Semantic analysis is an advisory subsystem; verdict orchestration lives in Evaluation.
- Portal cannot bypass application authorization by talking directly to storage.
- Generated schemas/clients live in clearly marked generated folders.
