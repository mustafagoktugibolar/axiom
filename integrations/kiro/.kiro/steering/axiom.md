---
inclusion: always
---

# Axiom governance

Follow `AGENTS.md` in this workspace. In short: call `governance.preflight_change` before any
design or edit; validate significant designs with `governance.validate_design` before
implementing; run `governance.validate_diff` before finishing. `REQUIRE_REVIEW`, `DENY`, or a
tool error means stop and ask a human — never treat it as allowed.

MCP server configuration: `.kiro/settings/mcp.json` (see `mcp.json.example` in the Axiom pack).
