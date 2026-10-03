# Workflow — Knowledge Onboarding

## Objective

Move from undocumented tribal knowledge to reviewed governance without pretending AI inference is truth.

## Sources

- existing ADRs;
- architecture docs;
- repository instructions;
- CI rules;
- repeated code patterns;
- PR review comments;
- incident/postmortem actions;
- security standards;
- dependency manifests;
- Backstage/catalog;
- interviews with senior engineers.

## Pipeline

```text
Discover
→ Extract candidates
→ Cluster duplicates
→ Map scope/owners
→ Human review
→ Accept/reject/refine
→ Add executable rule where possible
→ Observe
→ Promote enforcement
```

## Candidate record rules

Candidate must carry evidence:
- source URL/path/commit;
- excerpt hash/locator;
- extraction method;
- confidence;
- suggested owner.

Candidate cannot govern.

## Recommended adoption order

1. Hard security/compliance invariants already enforced elsewhere.
2. Existing explicit ADRs.
3. Stable platform standards.
4. Cross-repo contract/versioning rules.
5. High-frequency PR review guidance.
6. Incident-derived constraints.
7. Broader principles/goals.
8. Inferred patterns only after review.
