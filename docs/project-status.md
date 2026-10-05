# Axiom — Project Status

Durable implementation tracker. The backlog itself lives in
`axiom-engineering-governance-plane/.kiro/specs/axiom/tasks.md`; this file records
ordering, state, verification, and items needing a human. It does not restate the backlog.

**Last updated:** 2026-10-05

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
| API host: JWT auth, RBAC, tenant check, error mapping, rate limit, health, evaluation/review/receipt endpoints | done | HTTP-level tests through `WebApplicationFactory` (diff, PR, MCP, receipts, authz) |
| Governance registry infra (1.6-1.12) | done, merged | 23 Postgres integration tests (sync, rebuild, search, Git source) |
| System graph (Phase 2) | done, merged | unit tests + 8 Postgres integration tests |
| Policy engine (5.1-5.6, 5.9) | done, merged (rule coverage is representative, not exhaustive per rule) | 25 unit tests: rules, P3 order-independence, P9 errors never pass, waivers |
| Host composition | Api and Workers register application, infrastructure, governance, catalog, policy | 2 composition integration tests (WebApplicationFactory + /health/ready) |
| Phase 8.1-8.8: diff + PR validation (`POST /v1/evaluations/diff`, `/pr`): Git SCM adapter, scope recomputed from the diff, scope expansion vs. the validated design, policy engine run, design-approval lineage | done | 17 unit + 8 HTTP-level integration tests (real Git + Postgres) |
| Phase 8.9-8.11: GitHub check runs + Azure DevOps statuses (off by default), `docs/operations/branch-policy.md` | done | 16 unit tests (request shape, verdict mapping, token only to its own API) |
| Phase 9.1-9.3: MCP server at `/mcp` (stateless streamable HTTP, same auth/rate limit as REST), 11 tools, RFC 9728 resource metadata + 401 challenge; REST: `/v1/governance`, `/v1/impact`, `/v1/context/{id}`, `/v1/evaluations/{id}/findings/{code}` | done except `governance.propose_decision` (needs the candidate workflow of Phase 11) | 7 MCP-client integration tests over real HTTP, Git, Postgres |
| Phase 8.8 CLI `axiom-cli`: `evaluate-pr`, `evaluate-diff`, `preflight`, `receipt`, `validate-governance`; exit codes 0/10/20, 3 = no verdict (fails closed), 2 = usage; text/json/github/azure output; token only from `AXIOM_TOKEN`, only over https | done | 23 unit tests |
| Exception workflow (R10, R11, ADR-0006): `POST/GET /v1/exceptions/requests`, `POST .../{id}/decision`, MCP `governance.request_exception` / `get_exception_request`. Requests are validated by the same `GovernanceSetValidator` rules that guard Git (exemptable, governing, scope inside target, window <= 180 days, tracking issue), routed to target owners, decided once by someone other than the requester, audited append-only, and yield a draft record (`status: proposed`, `accepted` after approval). Approval waives nothing: only the record merged into the governance repository does | done (Axiom never writes to Git; a maintainer commits the draft). Expiry review (13.7) and `exception.expiring` events not built | 19 unit + 3 end-to-end tests incl. the full request -> approve -> merge -> waived lifecycle |
| Harness packs (9.4-9.6): portable `AGENTS.md`, Kiro steering + agent-stop hook + MCP config, Claude Code `CLAUDE.md` + hooks + MCP config (`integrations/`) | done; 9.7-9.9 (Codex, Cursor, Copilot) read `AGENTS.md`, no dedicated packs; 9.10 conformance tests not built | `axiom-cli` flags checked against `--help` |
| CI (`.github/workflows/ci.yml`): build + test, portal build | done | not yet run on a hosted runner |
| Basic portal (`src/Axiom.Portal`, Vue 3 + TS): governance search/filter, record detail with revision history and relations, evaluation/receipt lookup, review queue (raw); served same-origin by the API when `Axiom:Portal:Path` is set. Auth is a pasted bearer token (OIDC sign-in not built) | done (MVP-level; 10.3-10.4 graphs, 10.6-10.8 rich views not built) | `npm run build` (vue-tsc + vite) |
| Portal sign-in: OIDC authorization code + PKCE (`/v1/auth/config` advertises authority/client), Development-only `/v1/auth/dev-login`; no pasted tokens | done | 2 integration tests written (need Docker; not run in this session) |
| Deployment: Dockerfile (api incl. portal, workers), Helm chart `deploy/helm/axiom`, Kustomize base + dev/prod overlays, `--migrate` mode for the migration Job | done | `helm lint`/`template` and `kubectl kustomize` render clean; images and a live cluster install not tested |
| Governance seed + sync: `GovernanceSyncWorker` in Axiom.Workers registers configured sources (`Axiom:Workers:GovernanceSync:Sources`) and re-syncs every interval; Helm `governance.sources/credentials`, Kustomize overlay patches | done. NOTE: before this the Workers host ran no jobs at all (no sync, no outbox dispatch, no health jobs) | 4 unit tests (seed, per-source failure isolation, disabled, interval); chart renders |
| Everything else (semantic, onboarding, hardening) | not started; semantic is post-MVP per the PRD | - |

## How to resume (new terminal session)

1. Work is in `/Users/goktugibolar/dev/axiom` on `main`. The three subagent branches are merged; their
   worktrees under `.claude/worktrees/*` and `worktree-agent-*` branches can be removed.
2. Migrations are a single `Initial` (the DB is not deployed). After changing a persistence model run
   `dotnet ef migrations add <Name> -o Persistence/Migrations` from `src/Axiom.Infrastructure`, or, while
   still undeployed, delete the migrations and regenerate `Initial`.
3. Next slices in order: semantic analyzer (Phase 7) -> onboarding (Phase 11) -> portal (Phase 10) ->
   deploy/CI -> hardening (Phase 12).
4. Verify with `dotnet build Axiom.slnx && dotnet test Axiom.slnx` (Docker needed for Postgres tests).

## Operational notes

- **SCM fetching is deny-by-default.** `GitScmDiffSource` takes clone URLs from the System Graph, which
  repositories themselves declare. It fetches only from hosts listed in `Axiom:Scm:AllowedHosts` (or
  with credentials under `Axiom:Scm:Credentials:<host>`); local paths need `Axiom:Scm:AllowLocalRepositories`.
- **Hosts retry transient DB failures** (`EnableRetryOnFailure`). Any code that opens its own transaction
  must run under `Database.CreateExecutionStrategy()`; `CatalogWriter` did not and was fixed.
- A governance rule bound to a rule ID the engine does not know (for example the spec example
  `gateway-no-domain-db-access`) yields `POLICY_RULE_UNKNOWN` at REQUIRE_REVIEW (P9), not a pass.

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
