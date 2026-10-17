# Cursor pack

- `.cursor/rules/axiom.mdc`: always-applied project rule (`alwaysApply: true`).
- `.cursor/mcp.json.example`: copy to `.cursor/mcp.json`; the token comes from `${env:AXIOM_TOKEN}`.

Confident: `.cursor/rules/*.mdc` with `description`/`alwaysApply` frontmatter; `mcpServers` with
`url` and `headers` in `.cursor/mcp.json`; `${env:NAME}` interpolation.
Uncertain (not configured): Cursor agent hooks (`.cursor/hooks.json`) are version dependent, so no
diff-validation hook is shipped. Rely on the rule plus the CI check (`axiom-cli evaluate-pr`).
