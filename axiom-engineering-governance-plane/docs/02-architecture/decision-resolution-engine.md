# Decision Resolution Engine

## Purpose

Return the smallest authoritative governance bundle that is sufficient to evaluate a specific change.

## Why pure semantic search is insufficient

Embedding similarity answers “what text resembles this task?” It does not answer:
- which record is authoritative;
- whether it is current;
- whether a scoped exception applies;
- whether a more specific rule exists;
- whether two accepted records conflict;
- whether the affected system is a transitive dependency.

Therefore vector retrieval is supplementary, never the first/only resolver.

## Resolution algorithm

### Step 1 — Normalize request
Resolve:
- organization;
- repository and immutable ref;
- actor/harness;
- task;
- optional paths/design/diff;
- environment/change class.

### Step 2 — Resolve topology
Repository binding yields component/system/domain.
Declared and diff-derived resources/APIs/capabilities expand the scope.
Dependency traversal is bounded by relation types and depth.

### Step 3 — Select structurally applicable records
Filter by:
- status = accepted;
- validity window;
- scope intersection;
- authority visibility;
- applicable record kind.

### Step 4 — Match exceptions
An exception matches only if:
- active time window;
- approved;
- targets include the applicable record;
- evaluated scope is contained by exception scope;
- target record is exemptable.

### Step 5 — Apply precedence
Hard non-exemptable controls remain.
Waived records remain visible but marked `waivedBy`.
More-specific refinements apply only if legal.

### Step 6 — Detect conflicts
Conflict sources:
- explicit `conflictsWith`;
- contradictory constraints in compatible scope;
- semantic analyzer candidate conflict.

Explicit/structural conflict is authoritative. Semantic conflict requests review unless a human confirms it.

### Step 7 — Retrieve semantic candidates
Only now query embeddings/full-text over:
- already relevant domains/systems;
- neighboring dependencies;
- task/design keywords.

Candidates outside structural scope are advisory unless topology is incomplete.

### Step 8 — Build context bundle
Prioritize:
- blocking/non-exemptable;
- accepted decisions;
- active exceptions;
- required checks;
- known failure modes;
- system goals;
- advisory principles.

Do not dump full corpus.

## Pseudocode

```text
resolve(input):
  ctx = normalize(input)
  topology = graph.resolve(ctx)
  structural = registry.match(topology, ctx)

  accepted = structural
    .where(status == ACCEPTED)
    .where(valid_now)

  exceptions = resolve_exceptions(accepted, ctx, topology)

  effective = apply_exceptions(accepted, exceptions)
  effective = apply_specificity(effective)

  conflicts = detect_authority_conflicts(effective)

  semantic_candidates = semantic_retrieve(
      task=ctx.task,
      allowed_scope=topology.semantic_scope
  )

  return ContextBundle(
      topology,
      effective,
      exceptions,
      conflicts,
      semantic_candidates,
      snapshot
  )
```

## Specificity

Suggested comparison tuple:

```text
(
 exact_component,
 exact_repository,
 exact_path,
 exact_capability,
 exact_api_resource,
 exact_environment,
 exact_technology,
 system,
 domain,
 organization
)
```

Specificity breaks ties only among records that are legally refinable. It is not authority escalation.

## Conflict policy

If:
- two non-exemptable controls conflict → `BLOCK: GOVERNANCE_CONFIGURATION_ERROR`;
- two accepted refinable decisions conflict → `REQUIRE_REVIEW`;
- accepted decision conflicts with candidate → accepted decision wins; candidate noted;
- accepted record conflicts with expired exception → accepted record wins;
- semantic analyzer sees possible conflict → `WARN` or `REQUIRE_REVIEW`, never silent override.

## Explainability

Every selected/excluded record should have a trace:

```json
{
  "recordId": "ARCH-042",
  "selected": true,
  "reasons": [
    "status=accepted",
    "repository=gateway matched",
    "component=api-gateway matched",
    "capability=homepage-resolution matched"
  ],
  "exception": null,
  "specificityScore": [1,1,0,1,0,0,0,1,1,1]
}
```
