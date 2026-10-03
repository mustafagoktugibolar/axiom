# Personas and Use Cases

## Personas

### AI-assisted software engineer
Needs immediate context and low-friction guidance. Hates governance portals that require manual lookup.

### Staff/principal engineer
Needs decisions to scale beyond personal memory and review comments.

### Architecture owner
Needs lifecycle, relationships, exceptions, and conflict visibility.

### Platform / developer productivity engineer
Needs stable contracts and enforcement that work across tools.

### Security engineer
Needs hard controls, ownership, review paths, and auditability.

### Engineering manager
Needs operational metrics: recurring violations, review bottlenecks, stale ownership.

## High-value use cases

1. **Architecture conflict before code**
   - Task proposes business logic in an API gateway.
   - Preflight finds accepted decision forbidding it.
   - Agent proposes compliant alternative.

2. **Cross-repo contract change**
   - Agent modifies event schema in producer.
   - System graph identifies consumers.
   - Decision requires backward compatibility.
   - Design gate requires migration/version strategy.

3. **New technology introduction**
   - Agent adds a new cache/database/framework.
   - Significant-change classifier flags strategic technology choice.
   - Existing standard is consulted; new ADR required if deviation is intentional.

4. **Expired temporary exception**
   - Legacy component used a forbidden dependency under an exception.
   - Exception expires.
   - Next PR is blocked until migration or renewed authorized exception.

5. **Review lesson becomes permanent**
   - Reviewer repeatedly comments that messages need correlation IDs.
   - Candidate invariant is generated.
   - Team approves it and adds deterministic check.
   - Future agents get prevention, future PRs get enforcement.

6. **Impact analysis**
   - Engineer plans to remove an API.
   - System graph returns consuming components and repository owners.
   - Design requires communication/migration tasks.

7. **Conflicting accepted decisions**
   - Two current decisions apply and contradict.
   - Axiom does not guess.
   - It opens/links a review item for the responsible owners.
