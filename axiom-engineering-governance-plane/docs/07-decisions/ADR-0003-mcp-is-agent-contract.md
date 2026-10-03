# ADR-0003 — MCP Is the Primary Interactive Agent Contract

**Status:** Accepted  
**Date:** 2026-10-03

## Context
Axiom must support multiple AI coding harnesses without duplicating governance logic.

## Decision
Expose the stable interactive tool contract through MCP. Harness-specific instructions/hooks remain thin adapters. REST remains available for CI, portal, and automation.

## Consequences
- Vendor-neutral governance.
- Harness replacement does not change core authority.
- Adapter conformance tests are required.
