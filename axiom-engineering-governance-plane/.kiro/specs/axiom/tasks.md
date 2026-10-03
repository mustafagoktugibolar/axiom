# Axiom Implementation Tasks

This task plan assumes a production-oriented build, not a disposable POC.

## Phase 0 — Product and governance foundations

- [ ] 0.1 Ratify product vocabulary and authority model.
- [ ] 0.2 Approve governance record lifecycle and precedence.
- [ ] 0.3 Define significant-change classification policy.
- [ ] 0.4 Select initial architecture owners/approvers.
- [ ] 0.5 Create governance source repository and branch protection.
- [ ] 0.6 Publish JSON schemas for decisions, exceptions, goals, standards.
- [ ] 0.7 Define organization/system/component/repository identifiers.
- [ ] 0.8 Choose initial pilot repositories from different teams.

## Phase 1 — Governance registry

- [ ] 1.1 Implement governance Git parser.
- [ ] 1.2 Implement schema validation.
- [ ] 1.3 Implement lifecycle validation.
- [ ] 1.4 Implement ownership validation.
- [ ] 1.5 Implement relationships (`supersedes`, `refines`, `conflictsWith`, `related`).
- [ ] 1.6 Implement Git commit/path/revision provenance.
- [ ] 1.7 Implement projection tables in PostgreSQL.
- [ ] 1.8 Implement full rebuild from Git.
- [ ] 1.9 Implement incremental sync.
- [ ] 1.10 Emit governance change events.
- [ ] 1.11 Add stale/review-date detection.
- [ ] 1.12 Add governance search API.

## Phase 2 — System graph

- [ ] 2.1 Implement software entity model.
- [ ] 2.2 Implement typed relationships.
- [ ] 2.3 Implement repository bindings.
- [ ] 2.4 Implement ownership.
- [ ] 2.5 Add bounded dependency traversal.
- [ ] 2.6 Add impact analysis API.
- [ ] 2.7 Implement import from repository metadata.
- [ ] 2.8 Implement optional Backstage adapter.
- [ ] 2.9 Add graph quality checks for missing owners/bindings.
- [ ] 2.10 Add graph visualization endpoint.

## Phase 3 — Deterministic resolver

- [ ] 3.1 Implement scope matching.
- [ ] 3.2 Implement specificity tuple.
- [ ] 3.3 Implement lifecycle filtering.
- [ ] 3.4 Implement non-exemptable control handling.
- [ ] 3.5 Implement exception matching and expiry.
- [ ] 3.6 Implement precedence.
- [ ] 3.7 Implement authoritative conflict detection.
- [ ] 3.8 Produce explainable resolution trace.
- [ ] 3.9 Property-test resolution invariants.
- [ ] 3.10 Benchmark resolution at target scale.

## Phase 4 — Preflight service

- [ ] 4.1 Implement `preflight_change` use case.
- [ ] 4.2 Implement repository/ref validation.
- [ ] 4.3 Implement affected-scope estimation.
- [ ] 4.4 Return required decisions/rules.
- [ ] 4.5 Return required design/review actions.
- [ ] 4.6 Return governance snapshot identifier.
- [ ] 4.7 Implement idempotency/request fingerprints.
- [ ] 4.8 Add preflight caching.
- [ ] 4.9 Add preflight telemetry.

## Phase 5 — Policy engine

- [ ] 5.1 Define policy result contract.
- [ ] 5.2 Implement built-in path/file rules.
- [ ] 5.3 Implement dependency direction rule.
- [ ] 5.4 Implement forbidden package/import rule.
- [ ] 5.5 Implement public contract compatibility rule integration.
- [ ] 5.6 Implement required metadata/test rules.
- [ ] 5.7 Implement secure custom policy runner.
- [ ] 5.8 Add rule time/memory/network limits.
- [ ] 5.9 Version/hash every rule execution.
- [ ] 5.10 Add local CLI runner for developers.

## Phase 6 — Design workflow

- [ ] 6.1 Define design schema/template.
- [ ] 6.2 Implement significant-change classifier.
- [ ] 6.3 Implement design parser.
- [ ] 6.4 Validate required design fields.
- [ ] 6.5 Validate “consulted decisions”.
- [ ] 6.6 Detect unrecorded architectural choices.
- [ ] 6.7 Detect design/topology scope mismatch.
- [ ] 6.8 Persist design evaluation.
- [ ] 6.9 Add reviewer approval workflow.
- [ ] 6.10 Invalidate approval on material design change.

## Phase 7 — Semantic analyzer

- [ ] 7.1 Define provider abstraction.
- [ ] 7.2 Implement bounded evidence packet.
- [ ] 7.3 Implement typed structured outputs.
- [ ] 7.4 Add prompt injection/content boundary defenses.
- [ ] 7.5 Add potential-decision-conflict analyzer.
- [ ] 7.6 Add duplicate-solution analyzer.
- [ ] 7.7 Add “new architectural decision” detector.
- [ ] 7.8 Add candidate-ADR extraction.
- [ ] 7.9 Add confidence calibration.
- [ ] 7.10 Capture human overturn feedback.
- [ ] 7.11 Track false-positive metrics.
- [ ] 7.12 Support “no external LLM” scopes.

## Phase 8 — Diff and PR evaluation

- [ ] 8.1 Implement SCM diff fetch adapter.
- [ ] 8.2 Recompute scope from actual diff.
- [ ] 8.3 Detect scope expansion.
- [ ] 8.4 Run deterministic policies.
- [ ] 8.5 Compare implementation to validated design.
- [ ] 8.6 Produce final verdict.
- [ ] 8.7 Generate immutable receipt.
- [ ] 8.8 Expose CI-friendly CLI/API.
- [ ] 8.9 Implement Azure DevOps status integration.
- [ ] 8.10 Implement GitHub check integration.
- [ ] 8.11 Add branch policy documentation.

## Phase 9 — MCP and harness integrations

- [ ] 9.1 Implement MCP server.
- [ ] 9.2 Publish tool schemas.
- [ ] 9.3 Add OAuth/workload identity support.
- [ ] 9.4 Create portable `AGENTS.md` bootstrap.
- [ ] 9.5 Create Kiro steering/hook integration.
- [ ] 9.6 Create Claude Code hook integration.
- [ ] 9.7 Create Codex bootstrap/integration.
- [ ] 9.8 Create Cursor rules/hook integration.
- [ ] 9.9 Create Copilot instructions/integration.
- [ ] 9.10 Add integration conformance tests.

## Phase 10 — Portal

- [ ] 10.1 Governance explorer.
- [ ] 10.2 Record detail/revision history.
- [ ] 10.3 Decision graph.
- [ ] 10.4 System graph.
- [ ] 10.5 Evaluation/receipt detail.
- [ ] 10.6 Review queue.
- [ ] 10.7 Exception request/approval.
- [ ] 10.8 Governance health dashboard.
- [ ] 10.9 Search and filters.
- [ ] 10.10 Accessibility/security review.

## Phase 11 — Knowledge onboarding

- [ ] 11.1 Import existing ADRs.
- [ ] 11.2 Scan repository docs for candidate decisions.
- [ ] 11.3 Mine repeated PR review comments.
- [ ] 11.4 Mine incidents/postmortems for invariants.
- [ ] 11.5 Generate candidate records only.
- [ ] 11.6 Build human approval queue.
- [ ] 11.7 Add duplicate candidate clustering.
- [ ] 11.8 Measure governance coverage by repository.

## Phase 12 — Hardening

- [ ] 12.1 Threat model review.
- [ ] 12.2 Penetration test API/MCP/portal.
- [ ] 12.3 Sandbox escape testing for custom rules.
- [ ] 12.4 Load test target baseline.
- [ ] 12.5 Chaos test LLM/vector/Git/database degradation.
- [ ] 12.6 Backup/restore test.
- [ ] 12.7 Projection rebuild drill.
- [ ] 12.8 Receipt replay/reproducibility test.
- [ ] 12.9 SLO dashboards and alerts.
- [ ] 12.10 Runbooks.

## Phase 13 — Organizational rollout

- [ ] 13.1 Pilot in observe-only mode.
- [ ] 13.2 Baseline common violations.
- [ ] 13.3 Promote high-confidence rules to warning.
- [ ] 13.4 Promote agreed hard controls to block.
- [ ] 13.5 Train decision owners.
- [ ] 13.6 Establish monthly governance health review.
- [ ] 13.7 Establish exception-expiry review.
- [ ] 13.8 Expand to additional repositories.
- [ ] 13.9 Publish adoption scorecard.
- [ ] 13.10 Review product metrics at 30/60/90 days.
