# SCM fetch failure

Two paths touch source control: reading a diff for diff and pull-request evaluation (and governance sync), and publishing a status check back to the pull request.

## Symptoms

- Diff or PR evaluations return error code `SCM_UNAVAILABLE` (`The diff of '<repo>' could not be read: ...`). No verdict is stored; callers must treat it as failure, not success.
- Alert `AxiomScmStatusPublishingFailing` (`axiom_scm_status_failures_total` > 5 in 15 min): verdicts are computed but the status check does not reach the SCM; branch protection waits on a missing check.
- Governance sync errors for the same host (governance-sync.md); `head.error` in `GET /v1/admin/governance-source`.
- Evaluation latency alerts if fetches are slow rather than failing.

## Diagnosis

1. Which direction fails? Fetch: `SCM_UNAVAILABLE` in API responses and logs. Publish: the alert above, evaluations otherwise succeed.
2. Host allow-list and credentials (`Axiom:Scm`): the host must be in `AllowedHosts` or have `Credentials`; tokens are sent only to their own host. Local paths need `AllowLocalRepositories`.
   `kubectl -n axiom exec deploy/axiom-api -- printenv | grep Axiom__Scm` (token values are secrets; do not paste output).
3. Token validity/scopes with the provider (expired, revoked, missing repository access, missing status-write permission for publishing).
4. Mirror cache: bare mirrors live in `Axiom:Scm:CacheDirectory` (default under the temp directory). It is only a cache; a corrupt mirror shows as repeated fetch failures for one repository.
5. Network egress and DNS from the pod to the SCM host; provider rate limits.
6. Large diffs: more than `MaxChangedFiles` (2000) are reported as truncated, which is not a failure.

## Mitigation

- Rotate/fix the token (secret-rotation.md), then restart: `kubectl -n axiom rollout restart deploy/axiom-api deploy/axiom-workers`.
- Corrupt mirror: delete the mirror directory of the affected repository inside the pod (`kubectl -n axiom exec deploy/axiom-api -- rm -rf <CacheDirectory>/<mirror>`); it is re-fetched on the next request. The pods use an emptyDir for `/tmp`, so a pod restart also clears it.
- Status publishing: after the SCM recovers, re-trigger the pull-request evaluation (same head sha returns the cached evaluation; push an empty commit or re-run the CI step that calls `POST /v1/evaluations/pr`) so the status is published.
- Provider outage: wait; merges that require the Axiom check stay blocked, which is the intended fail-closed behavior.

## Escalate

Escalate to the platform owner when credentials are valid, the host is reachable and fetches still fail; or to the SCM administrator when the provider returns 5xx or rate limits.

## Do not

- Do not remove the Axiom check from required status checks, or mark the check manually, to unblock merges.
- Do not add hosts to `AllowedHosts` or enable `AllowLocalRepositories` in production to get around a failing fetch; clone URLs come from repository-declared data.
- Do not put tokens in the ConfigMap or in repository URLs.
