# Exception Model

## Purpose

Allow intentional, temporary deviation without weakening the underlying decision.

## Required fields

- stable exception ID;
- target governance IDs;
- scope;
- rationale;
- owner;
- approver;
- start time;
- expiry time;
- compensating controls if relevant;
- tracking issue/migration plan for temporary debt.

## Rules

1. Exceptions never edit the target decision.
2. Scope must be equal to or narrower than the target's applicable scope.
3. Expiry is mandatory unless an explicitly governed exception class permits otherwise.
4. `exemptable: false` records cannot be waived by normal exceptions.
5. An exception is itself authoritative only after approval.
6. Expired exceptions remain in history but stop applying automatically.

## Example

```yaml
schemaVersion: axiom.io/v1
kind: Exception
metadata:
  id: EXC-023
  title: Temporary legacy gateway homepage routing
  owners: [gui-platform]
spec:
  status: accepted
  targets: [ARCH-042]
  scope:
    repositories: [gateway]
    paths: ["src/Legacy/Homepage/**"]
  rationale: >
    Temporary compatibility during Homepage Service migration.
  startsAt: 2026-10-01T00:00:00Z
  expiresAt: 2027-01-01T00:00:00Z
  trackingIssue: GUI-912
  compensatingControls:
    - No new callers may use the legacy route.
```
