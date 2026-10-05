# Axiom

Axiom is an engineering governance plane. Your architecture decisions, standards and approved exceptions live
as files in a Git repository; Axiom turns them into checks that run **before** an engineer or AI agent
starts a change (preflight), **while** designing it, and **on the actual code** in CI (diff / pull request).
Every check leaves a tamper-evident receipt.

```
governance repo (Git)  ──sync──▶  Axiom  ◀── agents (MCP) · CI (axiom-cli) · people (portal)
 decisions, standards,             resolves what applies, runs deterministic rules,
 exceptions, catalog               records verdict + receipt
```

## Try it locally (5 minutes)

Needs Docker Desktop with Kubernetes enabled, and `kubectl`.

```powershell
powershell -File deploy/local/up.ps1     # builds the images, deploys with demo data, port-forwards
```

Open <http://localhost:8080>, click **Sign in**, and follow **Get started**:

1. the demo governance records are loaded,
2. run a **preflight** for the demo repository,
3. **validate a pull request**: the demo change imports a database driver into the gateway, which decision
   ARCH-001 forbids, so the result is **Blocked** with the exact file and rule,
4. connect your agent through MCP.

Then explore **Evaluations** (every check and its receipt), **Exceptions** (request a waiver, then sign in as a
second user, e.g. `reviewer`, to approve it), **System graph**, and **Set up** (connect your own governance
repository). Remove everything with `powershell -File deploy/local/down.ps1`.

## Use it for real

| You want to | Read |
|---|---|
| Deploy to a cluster (Helm or Kustomize), set up OIDC and secrets | [deploy/README.md](deploy/README.md) |
| Put your decisions in a governance repository | the demo in [deploy/local/demo/governance](deploy/local/demo/governance) and the portal's **Set up** page |
| Block bad pull requests in CI | [docs/operations/branch-policy.md](docs/operations/branch-policy.md) and `axiom-cli evaluate-pr` |
| Connect an AI agent (Kiro, Claude Code, others) | [integrations/README.md](integrations/README.md) |
| Understand the design | [axiom-engineering-governance-plane/](axiom-engineering-governance-plane/) (the specification) |
| See what is built and what is not | [docs/project-status.md](docs/project-status.md) |

## Repository layout

```
src/        Axiom.Domain · Application · Infrastructure (PostgreSQL, Git) · Api (REST + MCP + portal host)
            Workers (sync) · Mcp · Portal (Vue 3 + TypeScript) · PolicyRunner
tools/      axiom-cli (evaluate-pr, evaluate-diff, preflight, receipt, validate-governance)
tests/      unit, architecture, integration (real Git + PostgreSQL via Testcontainers), contract, e2e
deploy/     Dockerfile, Helm chart, Kustomize overlays, local trial scripts and demo data
integrations/  agent starter packs (AGENTS.md, Kiro, Claude Code)
```

Build and test: `dotnet build Axiom.slnx && dotnet test Axiom.slnx` (Docker is needed for the integration tests).
