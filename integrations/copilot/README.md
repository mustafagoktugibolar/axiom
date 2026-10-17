# Copilot pack

- `.github/copilot-instructions.md`: repository-wide custom instructions.
- `.vscode/mcp.json.example`: copy to `.vscode/mcp.json` for VS Code agent mode.

Confident: `.github/copilot-instructions.md`; VS Code `.vscode/mcp.json` with a top-level
`servers` object, `type: "http"`, `url` and `headers`.
Uncertain: `${env:AXIOM_TOKEN}` interpolation in `.vscode/mcp.json` (VS Code documents `${input:...}`
prompts; if `${env:...}` is unsupported in your version use an `inputs` password prompt instead,
still never an inline token). The Copilot coding agent takes MCP servers from repository settings,
not a file in the repository; configure `axiom` there with a secret named `AXIOM_TOKEN`. No hooks
are shipped; the CI check (`axiom-cli evaluate-pr`) is the enforcement.
