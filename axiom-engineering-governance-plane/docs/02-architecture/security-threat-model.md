# Security Threat Model

## Assets

- authoritative governance decisions;
- exception approvals;
- source/topology metadata;
- evaluation receipts;
- organization/repository mappings;
- credentials to SCM/catalog/LLM providers;
- custom executable rules.

## Threat actors

- compromised developer machine;
- malicious/compromised agent tool;
- malicious repository content/prompt injection;
- insider attempting unauthorized exception;
- compromised webhook sender;
- malicious custom rule;
- external model/provider data exposure;
- cross-tenant actor.

## Key threats and controls

### T1 — Agent claims governance passed without calling Axiom
Controls:
- CI revalidation by immutable commit SHA;
- server-issued evaluation/receipt IDs;
- branch protection.

### T2 — Prompt injection in repository tells agent to ignore governance
Controls:
- governance bootstrap has higher operational priority;
- hook/CI enforcement does not depend on model obedience;
- tool outputs are structured and trusted separately from repo text.

### T3 — Unauthorized decision acceptance
Controls:
- Git branch protection;
- CODEOWNERS;
- role-based approval;
- signed identity in audit;
- portal cannot bypass Git authority.

### T4 — Exception abuse
Controls:
- scoped target IDs;
- expiry required;
- dedicated approver role;
- non-exemptable controls;
- alerting on high-risk exceptions.

### T5 — Custom policy runner compromise
Controls:
- rootless isolated container;
- no default network;
- read-only source mount where possible;
- CPU/memory/time limits;
- no ambient cloud credentials;
- signed/versioned rule artifact;
- output schema validation.

### T6 — Sensitive code sent to external LLM
Controls:
- organization/provider policy;
- redaction;
- evidence minimization;
- local/self-hosted provider option;
- scopes marked `semanticExternalAllowed: false`.

### T7 — Cross-tenant leakage
Controls:
- tenant key on every request/table/cache key;
- authorization at query layer;
- no global vector search;
- tenant-scoped encryption/audit.

### T8 — Tampering with receipts
Controls:
- append-oriented storage;
- content digest;
- source commit identifiers;
- optional external WORM/object retention for regulated environments.

## Secrets

Never place secrets in governance files.
Use workload identity or managed secret stores for connectors.
MCP clients receive no raw connector credentials.

## Security review gates

Changes to:
- auth/authz;
- policy runner;
- exception model;
- receipt integrity;
- tenant isolation;
- external semantic providers

are always significant changes.
