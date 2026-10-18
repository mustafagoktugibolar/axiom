# Secret rotation

Secrets are plain Kubernetes Secrets today (External Secrets Operator is a deferred decision). The Secret `axiom-secrets` is read as environment variables when a pod starts, so a changed
value reaches Axiom only after a pod restart. Keys used by the shipped manifests: `connection-string` (database), `git-token-github` (governance and SCM fetch for github.com; optional), and with the
Helm dev mode `dev-signing-key` (never for production). Other tokens (LLM provider key, webhook secrets, SCM provider tokens) come from configuration keys such as `Axiom__Semantic__Anthropic__*`,
`Axiom__Events__Webhooks__N__Secret`, `Axiom__Scm__Providers__*`; keep them in the same Secret and reference them from the Deployment env.

## When

Scheduled rotation, suspected leak, staff change, or provider-forced expiry. After a leak, rotate first and investigate second.

## Procedure

1. Create the new credential at its source (database role/password, Git host token, provider key) **without** revoking the old one yet.
2. Update the Secret value. Replace one key and keep the others:

```bash
kubectl -n axiom create secret generic axiom-secrets --dry-run=client -o yaml \
  --from-literal=connection-string='<new>' \
  | kubectl -n axiom patch secret axiom-secrets --patch-file /dev/stdin
# or edit a single key:
kubectl -n axiom patch secret axiom-secrets -p "{\"stringData\":{\"git-token-github\":\"<new-token>\"}}"
```

   With Helm and `secrets.existingSecret`, the Secret lives outside the chart; change it as above. A chart-rendered Secret comes from values (`helm upgrade` with the new value).
3. Restart the pods that read it, workers and API (a rolling restart; replicas keep serving):
   `kubectl -n axiom rollout restart deploy/axiom-api deploy/axiom-workers` and wait with
   `kubectl -n axiom rollout status deploy/axiom-api` / `deploy/axiom-workers`.
4. Verify: `GET /health/ready` is healthy; worker logs show a clean cycle (`Governance published ...` or no `failed` lines); for tokens `GET /v1/admin/governance-source` shows `head.sha` without `head.error`;
   for the database run one evaluation or `GET /v1/receipts/chain/verify`.
5. Only now revoke the old credential at its source.

## Rollback

If verification fails, restore the previous value into the Secret, restart the same Deployments again, then investigate. This is why the old credential is kept until step 5.

## Escalate

Escalate to the security owner for any suspected leak (also review access logs of the old credential) and when a signing key for dev mode appears in a shared environment.

## Do not

- Do not put secrets in the ConfigMap, repository URLs, Helm `--set` on a shared CI log, or commit them.
- Do not rotate the database credential and revoke the old one in a single step; rolling pods hold open connections with the old one.
- Do not edit or delete rows in `axiom.receipt` as part of a rotation; receipts do not contain secrets.
- Do not skip the restart and assume pods pick up the Secret; environment variables are read at start.
