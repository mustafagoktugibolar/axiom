# Integration and Harness Adapters

## Principle

The governance contract is stable; harness adapters are disposable.

```text
Kiro --------\
Claude -------\
Codex --------- > MCP / REST contract -> Axiom
Cursor -------/
Copilot -----/
Future Agent/
```

## MCP first

Use MCP for interactive agent calls:
- preflight;
- context;
- design validation;
- impact;
- diff validation;
- explanations.

## Hooks second

Hooks enforce that the tool is actually called at the right lifecycle points.

Typical triggers:
- prompt submit / task start → preflight;
- before write/edit tools → verify preflight token/snapshot;
- before spec task → design approved if required;
- after edit batch → incremental diff check;
- before commit/PR → final diff validation.

## CI last and authoritative

Harness compliance is convenience and early feedback. CI is the enforcement boundary.

## Kiro adapter

Use:
- `.kiro/steering/` for bootstrap behavior;
- `.kiro/hooks/` for preflight/diff gate;
- MCP configuration for tools;
- Kiro Specs for formal significant-change artifacts.

## Claude Code adapter

Use:
- small `CLAUDE.md`/`AGENTS.md`;
- PreToolUse hooks to require governance state before writes;
- MCP for actual governance operations.

## Codex adapter

Use:
- small `AGENTS.md`;
- configured MCP/tools where environment supports them;
- CI enforcement independent of session behavior.

## Cursor / Copilot

Use their repository instruction/rule mechanisms only to bootstrap Axiom calls; never duplicate governance corpus.

## SCM adapters

### Azure DevOps
- PR status/check;
- repo/commit/diff fetch;
- branch policy integration;
- service hook ingestion.

### GitHub
- Checks API;
- pull request diff;
- CODEOWNERS/branch protection compatibility;
- webhooks.

### GitLab
- merge request pipeline/status;
- webhooks;
- diff fetch.

All adapters normalize to the same internal SCM port.
