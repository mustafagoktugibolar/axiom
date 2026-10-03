# Test Strategy

## Unit tests
- scope matcher;
- precedence;
- lifecycle;
- exception containment;
- verdict lattice;
- snapshot hashing.

## Property-based tests

Properties:
1. non-accepted records never govern;
2. expired exceptions never waive;
3. narrower exception never affects outside scope;
4. non-exemptable controls survive all normal exceptions;
5. same inputs/snapshot/rules yield same deterministic result;
6. unresolved accepted conflicts never produce `ALLOW`;
7. expanding diff scope cannot reduce required governance merely due to old design scope;
8. receipt references exact applied revisions.

## Architecture tests
- Domain does not depend on Infrastructure;
- MCP/REST share application use cases;
- semantic module cannot mutate governance registry;
- policy engine cannot grant exceptions;
- portal cannot access persistence directly.

## Contract tests
- MCP tool schemas;
- OpenAPI clients;
- SCM adapters;
- Backstage adapter;
- policy runner result schema.

## Integration tests
- Git merge → projection → snapshot;
- preflight end-to-end;
- exception approval/expiry;
- diff evaluation;
- receipt generation;
- DB rebuild.

## Semantic evaluation suite
Maintain labeled cases:
- true conflict;
- false conflict;
- new decision required;
- duplicate solution;
- irrelevant but semantically similar ADR.

Measure precision/recall and reviewer overturn rates.

## Security tests
- authz matrix;
- tenant isolation;
- webhook spoofing;
- prompt injection resilience;
- custom rule sandbox;
- malicious oversized inputs;
- path traversal;
- SSRF in connector inputs.

## Load tests
- burst preflight;
- parallel PR checks;
- large governance corpus;
- catalog sync during interactive load.
