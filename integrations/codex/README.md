# Codex pack

- `AGENTS.axiom.md`: workflow instructions; merge into the repository `AGENTS.md` (Codex reads it).
- `.codex/config.toml.example`: MCP server `axiom` using `bearer_token_env_var = "AXIOM_TOKEN"`.

Confident: `AGENTS.md` instructions; `[mcp_servers.<name>]` with `url` and `bearer_token_env_var`
for streamable-HTTP servers in `config.toml`.
Uncertain (not configured, verify against your Codex version): project-scoped `.codex/config.toml`
loading rules, and any lifecycle hook that would run `axiom-cli evaluate-diff` on completion.
Until verified, rely on the instructions plus the CI check (`axiom-cli evaluate-pr`).
