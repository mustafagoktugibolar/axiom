# MCP Contract

## Design rules

- Tool names are stable and vendor-neutral.
- Results are typed and explainable.
- Tools never expose connector credentials.
- Tools accept immutable SCM coordinates where possible.
- Evaluation tools return `evaluationId`, `snapshotId`, `verdict`, findings, and required actions.
- Agent clients must not infer `ALLOW` from tool failure.

## Tool: `governance.preflight_change`

### Input

```json
{
  "organization": "acme",
  "repository": "gateway",
  "ref": "feature/homepage",
  "task": "Resolve homepage by user role in the gateway",
  "paths": ["src/Homepage/**"],
  "harness": {
    "type": "kiro",
    "sessionId": "optional"
  }
}
```

### Output

```json
{
  "evaluationId": "ev_01...",
  "snapshotId": "gs_7b9...",
  "verdict": "REQUIRE_REVIEW",
  "significantChange": true,
  "resolvedScope": {
    "systems": ["gui-platform"],
    "components": ["api-gateway"],
    "repositories": ["gateway"],
    "capabilities": ["homepage-resolution"]
  },
  "applicableGovernance": [
    {
      "id": "ARCH-042",
      "revision": "7",
      "importance": "required",
      "summary": "Gateway must not contain business routing logic"
    }
  ],
  "requiredActions": [
    "Produce a design before editing",
    "Keep role-specific resolution in Homepage Service or request an exception"
  ],
  "findings": [
    {
      "code": "POTENTIAL_DECISION_CONFLICT",
      "severity": "REQUIRE_REVIEW",
      "governanceIds": ["ARCH-042"],
      "message": "Proposed task appears to move role-specific business routing into the gateway."
    }
  ]
}
```

## Tool: `governance.get_context`

Returns the bounded context bundle for an evaluation/scope.

Input:
- `evaluationId` OR explicit repo/task/scope.
- optional `includeRationale`.

Output:
- decisions;
- standards;
- goals;
- active exceptions;
- topology neighbors;
- known failure modes;
- required checks.

## Tool: `governance.query_decisions`

Search/browse with filters:
- text;
- IDs;
- owner;
- system/component/repository;
- status;
- kind;
- technology;
- tags.

Results distinguish authoritative vs historical/candidate.

## Tool: `governance.impact_analysis`

Input:

```json
{
  "repository": "producer-a",
  "change": {
    "kind": "api_or_contract",
    "entityRef": "api:orders/v2"
  },
  "depth": 2
}
```

Output:
- affected entities;
- relationship paths;
- owners;
- related governance;
- confidence/provenance.

## Tool: `governance.validate_design`

Input:
- evaluation/preflight ID;
- structured design or file reference;
- repository/ref.

Output:
- design version hash;
- verdict;
- missing sections;
- conflicts;
- decision candidates;
- required reviewers;
- approved scope.

## Tool: `governance.validate_diff`

Input:
- repository;
- base SHA;
- head SHA;
- optional preflight/design IDs.

Output:
- actual changed scope;
- scope expansion;
- policy runs;
- semantic findings;
- verdict;
- required actions.

## Tool: `governance.get_receipt`

Returns receipt by evaluation/receipt ID or repo+commit.

## Tool: `governance.propose_decision`

Creates a **candidate**, never accepted authority.

Input:
- suggested title/context/decision;
- proposed scope;
- evidence references;
- owner suggestion.

Output:
- candidate ID;
- Git PR/workflow reference.

## Tool: `governance.request_exception`

Creates an exception request for human approval.

It does not grant the exception.

## Tool: `governance.explain_finding`

Returns:
- selected rules;
- resolution trace;
- evidence;
- remediation options;
- authority owners.

## MCP error semantics

Never represent transport/internal failure as governance success.

Example:

```json
{
  "error": {
    "code": "GOVERNANCE_UNAVAILABLE",
    "retryable": true,
    "message": "Axiom could not complete required policy evaluation."
  }
}
```

Harness behavior for required gates: stop or require human fallback according to organization policy.
