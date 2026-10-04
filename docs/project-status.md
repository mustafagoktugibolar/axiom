# Axiom — Project Status

Durable implementation tracker. The backlog itself lives in
`axiom-engineering-governance-plane/.kiro/specs/axiom/tasks.md`; this file records
ordering, state, verification, and items needing a human. It does not restate the backlog.

**Last updated:** 2026-10-04

## Source of truth

`axiom-engineering-governance-plane/` is the authoritative specification and is kept
intact at the repository root. Precedence follows `MANIFEST.md`: `requirements.md` +
`design.md` win over other documents until an ADR supersedes them. Implementation-level
ADRs are added to `axiom-engineering-governance-plane/docs/07-decisions/` (ADR-0008+).

## Target repository structure

```text
axiom-engineering-governance-plane/   authoritative spec package (unchanged layout)
src/
  Axiom.Domain/          pure model: Governance, Catalog, Resolution, Policy,
                         Semantic, Evaluation, Audit (one namespace per module)
  Axiom.Application/     use cases + ports; the only entry point for REST, MCP, CLI
  Axiom.Infrastructure/  EF Core/PostgreSQL projections, Git source, YAML/JSON-schema
                         parsing, SCM / Backstage / LLM adapters, telemetry
  Axiom.Mcp/             MCP tool definitions (hosted by Axiom.Api at /mcp)
  Axiom.Api/             stateless REST + MCP host, OIDC, RBAC, health, rate limits
  Axiom.Workers/         sync, outbox dispatch, governance-health jobs
  Axiom.PolicyRunner/    isolated runner for custom (untrusted) rules
  Axiom.Portal/          Vue 3 + TypeScript portal
tests/
  Axiom.UnitTests/ Axiom.IntegrationTests/ Axiom.ContractTests/
  Axiom.ArchitectureTests/ Axiom.E2E/
governance/              Axiom's own governance records (dogfooding) + schemas
docs/                    architecture/, operations/, runbooks/, project-status.md
deploy/                  helm/, kustomize/, docker
tools/axiom-cli/         CI/local CLI
integrations/            harness starter packs (Kiro, Claude Code, Codex, Cursor, Copilot)
```

## Spec inconsistencies found and how they are resolved

| # | Inconsistency | Resolution |
|---|---|---|
| 1 | `design.md` §4.3 lists edge `GOVERNED_BY`; `system-graph.md` lists `SUPPORTS` instead. | Both are supported; `design.md` wins on conflict and neither excludes the other. |
| 2 | `exception.schema.json` allows status `expired`; lifecycle doc and R10 say expiry is automatic from `expiresAt`. | Expiry is computed from the validity window at evaluation time. A stored `expired` status is accepted on input and treated as non-applying. |
| 3 | `domain-model.md` example uses authority level `architecture`, absent from the schema enum. | Schema enum is canonical; the example value is rejected by validation. |
| 4 | `rest-api.yaml` omits endpoints the design requires (catalog, reviews, onboarding, health). | Contract is extended additively; existing operations keep their shape. |
| 5 | Brief says the remote is empty; it holds an `Initial commit` (README + .gitignore). | Built on top of that commit; no history rewritten. |

## Backlog reconciliation (implementation dependency order)

The spec phases are mostly already in dependency order. Adjustments:

1. **Foundation** (new, precedes Phase 1): solution, build rules, architecture tests, CI.
2. **Phase 0.6 / 0.7** — schemas and identifier formats (prerequisite of the parser).
3. **Phase 1** — governance registry.
4. **Phase 2** — system graph (needed by the resolver's topology step).
5. **Phase 3** — deterministic resolver.
6. **Phase 5.1–5.6, 5.9** — built-in policy engine (pulled ahead of Phase 4: preflight returns required checks).
7. **Phase 4** — preflight; **8.7** receipts pulled forward (every evaluation needs a receipt).
8. **Phase 6** — design workflow. **Phase 8** — diff / PR evaluation, CLI, SCM checks.
9. **Phase 9** — MCP + harness packs. **Enterprise epic** (OIDC, RBAC, tenancy) lands with the API host, not at the end.
10. **Phase 7** — semantic analyzer (after deterministic path is trustworthy, per PRD).
11. **Phase 11** — knowledge onboarding. **Phase 10** — portal (built incrementally as APIs land).
12. **Phase 5.7–5.8** — isolated custom policy runner. **Phase 12** — hardening.

## Status

| Area | State | Verification |
|---|---|---|
| Foundation: solution, CPM, warnings-as-errors, layering architecture tests | done | build clean, 6 architecture tests |
| Tasks 0.6, 0.7, 1.1-1.5: schemas, record parser, set validation, snapshot id | done (infrastructure for 1.6-1.12 below) | unit tests |
| Phase 3: deterministic resolver, P1/P3/P4/P5/P6 property tests | done (3.10 benchmark open) | unit + FsCheck |
| Phase 4: preflight, evaluation pipeline, receipts (hash chain), evaluation store | done (4.8 cache = deterministic evaluation IDs; 4.9 metrics defined) | unit + 1 Postgres test |
| Phase 6: design parser, gate, classifier, review workflow | done except 6.7 depth checks via graph | unit tests |
| API host: JWT auth, RBAC, tenant check, error mapping, rate limit, health, evaluation/review/receipt endpoints | built, no HTTP-level tests yet | build only |
| Governance registry infra (1.6-1.12) | done, merged | 23 Postgres integration tests (sync, rebuild, search, Git source) |
| System graph (Phase 2) | done, merged | unit tests + 8 Postgres integration tests |
| Policy engine (5.1-5.6, 5.9) | done, merged (rule coverage is representative, not exhaustive per rule) | 25 unit tests: rules, P3 order-independence, P9 errors never pass, waivers |
| Host composition | Api and Workers register application, infrastructure, governance, catalog, policy | 2 composition integration tests (WebApplicationFactory + /health/ready) |
| Everything else (diff/PR, MCP, CLI, semantic, onboarding, portal, deploy, CI, hardening) | not started | - |

## How to resume (new terminal session)

1. Work is in `/Users/goktugibolar/dev/axiom` on `main`. The three subagent branches are merged; their
   worktrees under `.claude/worktrees/*` and `worktree-agent-*` branches can be removed.
2. Migrations are a single `Initial` (the DB is not deployed). After changing a persistence model run
   `dotnet ef migrations add <Name> -o Persistence/Migrations` from `src/Axiom.Infrastructure`, or, while
   still undeployed, delete the migrations and regenerate `Initial`.
3. Next slices in order: diff/PR evaluation (Phase 8) -> MCP server (Phase 9) -> CLI -> exceptions
   workflow -> semantic analyzer (Phase 7) -> onboarding (Phase 11) -> portal (Phase 10) ->
   deploy/CI -> hardening (Phase 12).
4. Verify with `dotnet build Axiom.slnx && dotnet test Axiom.slnx` (Docker needed for Postgres tests).

## Decisions requiring human input

These tasks are organizational and cannot be completed by code. Axiom ships the mechanism;
the choice is the owner's.

- **0.4** initial architecture owners/approvers — who they are.
- **0.5** branch protection on the governance repository — requires repo admin action.
- **0.8** pilot repositories.
- **12.2** penetration test — needs an independent tester.
- **Phase 13** organizational rollout (pilot, training, monthly reviews, scorecards).

## Blocked items

None.

## Technical debt deliberately accepted

None.
