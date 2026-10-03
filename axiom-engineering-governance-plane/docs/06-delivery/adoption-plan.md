# Adoption Plan

## Principle

Do not begin by blocking developers. Begin by proving relevance.

## 30 days — Observe
- onboard 3–5 representative repos;
- import explicit ADRs/standards;
- map system topology;
- run preflight and CI in non-blocking mode;
- measure false positives and missing decisions.

## 60 days — Warn / require review
- promote high-confidence rules;
- require design for clearly significant changes;
- introduce review queue;
- add expiring exceptions;
- integrate second AI harness.

## 90 days — Block selected hard controls
- only deterministic, agreed rules;
- branch policy for governance receipt;
- governance health review;
- publish adoption/quality metrics.

## Team operating model

### Platform team owns
- Axiom runtime;
- schemas;
- integration SDKs;
- baseline policy framework.

### Domain/system owners own
- scoped decisions;
- topology accuracy;
- review and supersession.

### Security owns
- non-exemptable controls;
- security exception policy.

### Developers own
- proposing missing decisions;
- accurate designs;
- remediation of findings.

## Anti-patterns

- importing every wiki page as authoritative;
- turning all guidance into `BLOCK`;
- letting AI auto-accept decisions;
- requiring formal design for trivial changes;
- using vector similarity as scope/precedence;
- copying central governance into every repo.
