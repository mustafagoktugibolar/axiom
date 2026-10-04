# Branch policy: Axiom as the merge gate

CI is the final enforcement boundary (ADR-0007). A local agent's "pass" is never trusted: the pull-request
gate re-evaluates the immutable commit SHAs on the server, independently of the harness (R8).

## What the gate does

`POST /v1/evaluations/pr` (CLI: `axiom-cli evaluate-pr`) takes `repository`, `baseSha`, `headSha`,
`pullRequest` and, for significant changes, `designEvaluationId`. Axiom then:

1. fetches the diff between the two SHAs from the repository's clone URL (System Graph),
2. recomputes the scope from the changed files and resolves governance as a hard gate
   (an unresolved authority conflict blocks),
3. runs every deterministic rule bound by an applicable record on the changed files,
4. checks the design lineage: the referenced design evaluation must exist, be approved (a
   `REQUIRE_REVIEW` design needs a recorded review), and still cover the diff; a diff that exceeds it is
   reported as `SCOPE_EXPANSION`,
5. issues a receipt for the exact head SHA (`GET /v1/receipts?repository=...&commitSha=...`).

| Verdict | Meaning | CLI exit code | Check state (GitHub / Azure DevOps) |
|---|---|---|---|
| `ALLOW`, `ALLOW_WITH_WARNINGS` | merge allowed | 0 | `success` / `succeeded` |
| `REQUIRE_REVIEW` | a named human authority must approve | 10 | `action_required` / `pending` |
| `BLOCK` | deterministic violation, rejected design, or unresolved conflict | 20 | `failure` / `failed` |
| (Axiom unavailable, SCM unreadable) | **no verdict**; fail closed | 3 | not published |

An internal failure is never reported as success: the API answers `503` with a retryable error code and
the CLI exits 3, so the required check cannot pass.

## Configuration

```jsonc
// Axiom.Api
"Axiom": {
  "Scm": {
    "AllowedHosts": [ "github.com" ],              // hosts Axiom may fetch from (deny by default)
    "Credentials": { "github.com": { "Token": "<read-only repo token>" } },
    "PublishStatus": true,                          // push the verdict as a check (default: off)
    "PublicBaseUrl": "https://axiom.example.com",   // links the check to the evaluation
    "Providers": {
      "github.com":    { "Type": "GitHub",      "Token": "<checks:write token>" },
      "dev.azure.com": { "Type": "AzureDevOps", "Token": "<PAT with Code (status)>" }
    }
  }
}
```

Tokens come from the secret store or environment (`Axiom__Scm__Providers__github.com__Token`). A
provider token is sent only to its own API (`https://api.github.com`, the configured GitHub Enterprise
`ApiBaseUrl`, or the Azure DevOps host of the clone URL); redirects are not followed.

## GitHub

1. Create a GitHub App (or a fine-grained token) with `checks: write` and `contents: read`.
2. In the repository's branch protection (or ruleset) for the protected branch, enable
   **Require status checks to pass** and select **Axiom governance** (the check name; configurable
   with `Providers:<host>:CheckName`).
3. Enable **Require branches to be up to date** so the evaluated head SHA is the merge candidate.
4. CI step (runs on `pull_request`):

   ```yaml
   - run: axiom-cli evaluate-pr --repository ${{ github.repository }}
            --base ${{ github.event.pull_request.base.sha }}
            --head ${{ github.event.pull_request.head.sha }}
            --pull-request ${{ github.event.pull_request.number }}
     env: { AXIOM_URL: https://axiom.example.com, AXIOM_TOKEN: ${{ secrets.AXIOM_TOKEN }} }
   ```

   With `PublishStatus` on, Axiom also posts the check itself; the CI step then only needs to fail on a
   non-zero exit code.

## Azure DevOps

1. Create a PAT (or service connection) with **Code (status)**.
2. Repos → Branches → *branch policies* → **Status checks** → add `governance/Axiom governance`,
   *Required*, *Reset status whenever there are new changes*.
3. Pipeline step:

   ```yaml
   - script: >
       axiom-cli evaluate-pr --repository $(Build.Repository.Name)
       --base $(System.PullRequest.TargetCommitId?) --head $(Build.SourceVersion)
       --pull-request $(System.PullRequest.PullRequestId)
     env: { AXIOM_URL: $(AxiomUrl), AXIOM_TOKEN: $(AxiomToken) }
   ```

   Resolve the base SHA with `git merge-base origin/$(System.PullRequest.TargetBranchName) HEAD` when the
   pipeline does not provide it.

## Human review

`REQUIRE_REVIEW` is cleared by a recorded review of the design evaluation (`POST /v1/evaluations/{id}/review`),
bound to the design's material hash. Re-running the gate after the approval produces a new evaluation
(the clearance state is part of its identity) and, if nothing else is open, `ALLOW`. A changed design,
or a diff that expands beyond it, invalidates the approval.

## Exceptions

A waiver applies only through an active, scoped, unexpired exception record in the governance
repository. A waived finding stays visible in the evaluation and receipt. Non-exemptable controls cannot
be waived.
