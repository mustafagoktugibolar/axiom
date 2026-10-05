# Axiom governance bootstrap

This repository is governed by Axiom. Governance records (decisions, standards, goals,
exceptions) are authoritative over your plan. Use the `axiom` MCP server (`/mcp`).

## Required workflow

1. **Before designing or editing**, call `governance.preflight_change` with the repository,
   branch, a plain-language `task`, and the `paths` you expect to touch.
2. If the result says the change is **significant**, write a design and call
   `governance.validate_design` **before** implementing. Implement only the validated scope.
3. If any call returns `REQUIRE_REVIEW`, `DENY` or an error: **stop** and surface it to a human.
   A tool error or timeout is never `ALLOW`.
4. Before finishing, call `governance.validate_diff` with the base and head commit SHAs.
   The diff is authoritative over your plan; scope expansion beyond the design must be reported.
5. Use `governance.explain_finding` to understand a finding. Do not work around it.
6. If a rule must be waived, call `governance.request_exception`. Never assume approval:
   only a record merged into the governance repository waives anything.

## Never

- Treat a missing or failed governance call as permission.
- Edit governance records to make your own change pass.
- Approve your own exception or design review.

CLI equivalent for CI and local checks: `axiom-cli preflight | evaluate-diff | evaluate-pr`
(token only via `AXIOM_TOKEN`, https only; exit 0 = pass, 10 = review, 20 = deny, 3 = no verdict).
