# Axiom governance (Codex)

Codex reads `AGENTS.md`. Append this content to the repository `AGENTS.md`, or keep
the portable `agents-md/AGENTS.md` and add this section.

## Required workflow

1. Before designing or editing, call `governance.preflight_change` with the repository, branch,
   a plain-language `task` and the `paths` you expect to touch.
2. If the change is significant, write a design and call `governance.validate_design` before
   implementing. Implement only the validated scope.
3. After implementing, and before finishing, call `governance.validate_diff` with the base and
   head commit SHAs. The diff is authoritative over your plan; report any scope expansion.
4. `REQUIRE_REVIEW` or `BLOCK` from any governance call means stop and surface it to a human.
5. A tool error, timeout or missing answer is never `ALLOW`. Stop and report it.
6. Use `governance.explain_finding` to understand a finding; do not work around it. To waive a
   rule call `governance.request_exception`; only a merged governance record waives anything.

## Never

- Treat a missing or failed governance call as permission.
- Edit governance records to make your own change pass.
- Approve your own exception or design review.

The MCP server `axiom` is served at `/mcp` and reads its token from the `AXIOM_TOKEN`
environment variable. Never write a token into any file. The CI check (`axiom-cli evaluate-pr`)
is the enforcement; this file only makes the agent comply earlier.
