# Engineering Governance Bootstrap

This repository uses Axiom as the authoritative engineering-governance plane.

Before planning or implementing a non-trivial change:

1. Call `governance.preflight_change`.
2. Read every governance item marked required.
3. If the change is classified significant, produce and validate a design before editing.
4. If Axiom returns `BLOCK`, do not proceed.
5. If Axiom returns `REQUIRE_REVIEW`, surface the unresolved issue; do not claim approval.
6. After implementation, call `governance.validate_diff`.
7. Do not treat local repository patterns as authority when they conflict with an accepted Axiom record.
8. If you discover an apparent missing/contradictory decision, propose a candidate rather than inventing policy.

CI performs independent final governance validation.
