# Harness starter packs

Copy the pack for your tool into a governed repository, plus `agents-md/AGENTS.md` (the portable
bootstrap every pack points at). Set `AXIOM_TOKEN`, `AXIOM_URL` and (Claude Code hook) `AXIOM_REPOSITORY` in the environment; never commit it.

| Pack | Contents |
|---|---|
| `agents-md/` | Portable `AGENTS.md` bootstrap (Codex, Cursor, Copilot and others read it) |
| `kiro/` | Steering file, agent-stop hook, MCP config example |
| `claude-code/` | `CLAUDE.md`, hooks in `.claude/settings.json`, MCP config example |
| `codex/` | Workflow section for `AGENTS.md`, `config.toml` MCP example |
| `cursor/` | `.cursor/rules/axiom.mdc` always-on rule, `.cursor/mcp.json` example |
| `copilot/` | Copilot custom instructions, `.vscode/mcp.json` example |

The hooks only remind and verify; enforcement is the CI check (`axiom-cli evaluate-pr`, see
`docs/operations/branch-policy.md`). Check the `axiom-cli` flags in your hook match the installed
version (`axiom-cli --help`).

Each pack README lists which product formats are confirmed and which are uncertain. Pack
conformance (real MCP tool names, real `axiom-cli` commands and flags, no inline secrets, mandatory
workflow statements) is enforced by `IntegrationPackConformanceTests` in `tests/Axiom.UnitTests`.
