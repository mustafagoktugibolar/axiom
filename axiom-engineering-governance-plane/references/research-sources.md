# Research Sources and Design Influences

Accessed / reviewed for this design on 2026-10-03.

## OpenAI — Harness engineering
https://openai.com/index/harness-engineering/

Key influence:
- repository knowledge as system of record;
- short `AGENTS.md` as a map rather than a giant manual;
- strict architectural boundaries and mechanical enforcement;
- recurring cleanup/entropy control.

## Adobe — Repository Harnesses for AI Coding Agents
https://opensource.adobe.com/ai-repo-harness-guide/00-Introduction/
https://opensource.adobe.com/ai-repo-harness-guide/04-Harness-Components/

Key influence:
- repository harness as a control system;
- explicit invariants and portable agent context;
- separating shared/open formats from tool-specific configuration.

## Kiro — Specs, Steering, Hooks, MCP
https://kiro.dev/docs/getting-started/first-project/
https://kiro.dev/docs/specs/quick-plan/
https://kiro.dev/docs/web/steering
https://kiro.dev/docs/hooks/

Key influence:
- requirements → design → tasks workflow;
- persistent steering;
- event-driven hooks;
- MCP extension model;
- explicit review gates for complex work.

## Sensei — Architecture governance for AI coding agents
https://github.com/globulario/sensei

Key influence:
- preflight/briefing before edits;
- architectural model/graph;
- explicit authority and bounded change;
- MCP/hooks/CI combination.

## Archgate — Executable ADRs
https://docs.archgate.dev/
https://docs.archgate.dev/concepts/adrs/
https://docs.archgate.dev/concepts/rules/

Key influence:
- ADR as human/agent-readable intent plus executable machine rule;
- prevention + detection + learning loop.

## Backstage — Software Catalog and AI/MCP
https://backstage.io/docs/features/software-catalog/system-model/
https://backstage.io/docs/ai/

Key influence:
- system/component/API/resource topology;
- ownership and dependency modeling;
- MCP-accessible internal developer platform concepts.

## Research — Architecture Without Architects
https://arxiv.org/abs/2604.04990

Key influence:
- AI coding agents make implicit architecture decisions;
- “vibe architecting” risk;
- need for explicit decision records and review/tooling.

## Interpretation

Axiom deliberately combines these ideas but is not a copy of any one product. The core proposed gap is an enterprise-wide, vendor-neutral **Engineering Governance Plane** that joins:
- authoritative organizational decisions;
- multi-repository system topology;
- preflight/design/diff/PR evaluation;
- deterministic + semantic checks;
- explicit human authority;
- auditable receipts.
