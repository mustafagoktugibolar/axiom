# Product Requirements Document

## Executive summary

Axiom is an enterprise control plane for AI-assisted software development. It centralizes accepted technical decisions and system topology, then injects the right subset into agent workflows before design and code changes. It independently validates implementation at PR time.

## Problem statement

As AI-assisted development expands across repositories, local agent instructions become inconsistent and organizational knowledge fragments. Engineers repeatedly rediscover constraints, unintentionally contradict decisions, and create new architecture implicitly.

## Goals

- Prevent known architecture violations before they are implemented.
- Detect cross-system conflicts earlier than code review.
- Reduce architecture-review repetition.
- Make decisions and exceptions durable and auditable.
- Make governance portable across AI harnesses.
- Allow organizations to progressively automate repeatable governance.

## User stories

### Developer
As a developer, I want the agent to retrieve only the decisions relevant to my task so I do not manually search documentation.

### Architect
As an architect, I want a decision to influence all relevant repositories and agents once accepted.

### Reviewer
As a reviewer, I want a PR to show which architecture decisions were consulted and which checks passed.

### Platform engineer
As a platform engineer, I want a standard MCP/CI integration instead of maintaining separate rule systems per AI vendor.

### Security engineer
As a security engineer, I want non-exemptable controls and auditable exceptions.

### Engineering manager
As a manager, I want to see recurring governance violations and stale decisions so we can improve the system rather than repeatedly correct people.

## Product surfaces

- MCP server
- REST API
- CLI
- Web portal
- SCM checks
- Event stream/webhooks
- Harness starter packs
- Governance Git repository templates

## MVP boundary vs production target

The first useful production slice includes:
- Git-backed records;
- system/repo/component mapping;
- deterministic scope resolver;
- preflight MCP;
- design template/validation;
- CI diff checks;
- receipts;
- basic portal/search;
- Kiro + one additional harness adapter.

Semantic conflict detection should ship after deterministic resolution is trustworthy.

## Out of scope

- automated architecture ownership replacement;
- automatic approval of AI-created decisions;
- generic enterprise knowledge management;
- mandatory graph database;
- source-code generation.
