# Workflow — Drift and Learning Loop

## Drift sources

- code no longer matches accepted decisions;
- decision no longer matches desired architecture;
- exception becomes permanent debt;
- topology becomes stale;
- repeated semantic findings reveal missing rule;
- review repeatedly overrides a rule.

## Scheduled governance health jobs

- find accepted records past review date;
- find exceptions expiring soon;
- find exceptions repeatedly renewed;
- find records never applied for long periods;
- compare hard rules to repository violations;
- find graph entities without owners;
- find contradictory/duplicate candidate decisions;
- find semantic findings with high reviewer-overturn rate.

## Learning loop

```text
Violation / Review / Incident
→ classify lesson
→ can it be deterministic?
   yes → add/update rule
   no  → add/refine decision/semantic review
→ approve
→ observe
→ measure false positives
→ promote enforcement
```

## Critical constraint

The system may propose improvements automatically, but changes to authoritative organizational state require approved workflow.
