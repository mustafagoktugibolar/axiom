# Technical Steering — Axiom

## Architectural style

Use a modular service architecture with strong internal boundaries. Start as a **modular monolith + asynchronous workers** unless scale or organizational ownership proves a service split necessary.

The authoritative governance state is Git-versioned. Runtime databases are projections/caches and must be rebuildable.

## Reference stack

- **Runtime/API:** ASP.NET Core / .NET 10
- **Database:** PostgreSQL
- **Semantic retrieval:** pgvector extension
- **UI:** Vue 3 + TypeScript
- **Telemetry:** OpenTelemetry
- **Container runtime:** Kubernetes-compatible OCI containers
- **Policy execution:** in-process for trusted built-ins; isolated container runner for custom/untrusted checks
- **Authentication:** OIDC/OAuth2 via enterprise identity provider
- **Authorization:** RBAC + resource/scope-aware policy
- **Source control integrations:** GitHub/Azure DevOps/GitLab adapters
- **Catalog integration:** optional Backstage adapter
- **Agent integration:** MCP first, harness-specific thin adapters second

## Hard technical rules

1. Git is authoritative for decisions, standards, principles, goals, exceptions, and schemas.
2. PostgreSQL is not the source of truth for governance content; it holds indexed/projection state.
3. LLM output cannot directly transition a decision to `accepted`.
4. Semantic analysis cannot silently override deterministic policy results.
5. A `BLOCK` verdict must always include stable machine-readable reasons and evidence.
6. Unresolved conflicts between authoritative records result in `REQUIRE_REVIEW`, or `BLOCK` if a hard prerequisite is missing.
7. All externally visible governance objects are versioned and addressable by stable ID.
8. Every evaluation produces a reproducible snapshot identifier.
9. Agent-local `AGENTS.md`, `CLAUDE.md`, `.cursor/rules`, or Kiro steering is bootstrap/configuration only—not the canonical decision store.
10. Runtime projections must be re-creatable from the governance repository and catalog sources.

## Module boundaries

- `Catalog`: systems, components, repositories, APIs, resources, ownership, dependencies.
- `Governance`: decisions, standards, principles, goals, exceptions, lifecycle.
- `Resolution`: scope matching, precedence, conflicts, context bundles.
- `Policy`: deterministic executable rules.
- `Semantic`: LLM-assisted conflict/intent analysis.
- `Evaluation`: preflight, design, diff, PR verdicts.
- `Integration`: MCP, REST, events, SCM, Backstage, harness adapters.
- `Audit`: receipts, evidence, snapshots, actor identity.
- `Portal`: human review and exploration.

No module may write another module's state except through its public application contract.
