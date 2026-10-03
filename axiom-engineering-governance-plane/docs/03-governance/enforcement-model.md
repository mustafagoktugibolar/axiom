# Enforcement Model

## Verdicts

### INFO
Context only.

### WARN
Change may proceed; finding must be visible.

### REQUIRE_REVIEW
Human authority required before protected merge/next stage.

### BLOCK
Mechanically disallowed or prerequisite missing.

## Source hierarchy

1. Non-exemptable hard control
2. Valid exception for exemptable target
3. Accepted scoped decision/invariant/standard
4. Principles/goals
5. Advisory/candidate/inferred information

## Prevention vs detection

### Prevention
Preflight/design context shapes agent behavior before code.

### Detection
Deterministic checks validate code/config after generation.

### Judgment
Semantic review covers intent/rationale gaps.

### Authority
Human review resolves ambiguous/conflicting organizational choices.

## When to make a rule deterministic

Prefer deterministic enforcement if violation can be expressed as:
- AST/import dependency;
- file/path relationship;
- manifest/config property;
- API/schema compatibility;
- required annotation;
- package/version restriction;
- test/ownership presence;
- deployment/security manifest rule.

Avoid LLM checks for those.

## Semantic gating policy

Default:
- confidence <0.60 → INFO;
- 0.60–0.80 → WARN;
- >0.80 with direct accepted-record evidence → REQUIRE_REVIEW.

Do not use generic model confidence as mathematical truth. Tune against labeled organization examples.

Semantic `BLOCK` is disabled by default.

## Progressive rollout

Each rule moves through:
1. Observe
2. Warn
3. Require review
4. Block

Promotion requires measured false-positive rate and owner approval.
