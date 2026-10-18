# Custom policy runner (plan items 5.7 and 5.8)

Custom rules are untrusted. Axiom never loads assemblies or scripts from governance repositories. A custom
rule is a **declarative JSON definition** interpreted by `Axiom.PolicyRunner`, a separate OS process.
Off by default: `Axiom:Policy:CustomRunner:Enabled=false` registers no custom rule at all.

## Rule language (`axiom-custom-rule/v1`)

```json
{
  "schema": "axiom-custom-rule/v1",
  "parameters": { "word": null, "owner": "platform" },
  "select": { "paths": ["src/**"], "exclude": ["src/gen/**"], "kinds": ["added", "modified"] },
  "checks": [
    { "type": "forbid-regex", "pattern": "{{word}}", "scope": "added-lines", "message": "Do not use {{word}}." }
  ]
}
```

* `parameters`: name to default string; `null` means required. `{{name}}` is substituted once into
  strings (never re-expanded). Binding `with` values must be declared parameters.
* `select`: which changed files the per-file checks examine (globs as in built-in rules; default all).
* Checks (max 20): `forbid-regex` (`scope`: `added-lines` default or `content`; violation per matching line),
  `require-regex` (each selected non-deleted file must match), `forbid-path` (every selected change is a
  violation), `require-file` (`path` glob must exist in the tree), `json-path` (`path` such as
  `$.a.b[0]`, `$.a[*].c`, `$['x']`; `op` is `exists`, `absent`, `equals`, `not-equals`, `matches` with `value`).
* Strict parsing: unknown properties or check types, oversized definitions (64 KB), patterns over 1000
  characters are errors. Regexes use .NET `NonBacktracking` (linear time; no lookaround or backreferences).
  Unparseable JSON content under `json-path` is a violation, not a pass.

Rule ID is `custom/<name>`; the namespace cannot shadow built-ins. Rule version is
`<declared>+spec.<sha256 prefix of the definition>`, so a changed definition changes the version and the
rule hash recorded in receipts (5.9).

## Protocol

Parent writes one UTF-8 JSON `RunnerRequest` to stdin (protocol 1: rule ID, definition, parameters, changes with
contents and added lines, and only the *paths* of the tree) and reads one JSON `RunnerResponse` from stdout
(`outcome` passed/violated/error, bounded violations, or an error `code`). Exit code 0 on any protocol result;
non-zero means crash. No files, sockets or HTTP clients are used by the runner.

## Limits and error codes (always `PolicyOutcome.Error`, never a pass; P9)

| Code | Cause |
| --- | --- |
| `CUSTOM_RULE_TIMEOUT` | wall clock exceeded (`TimeoutSeconds`, default 10); process tree killed |
| `CUSTOM_RULE_OUTPUT_TOO_LARGE` | stdout above `MaxOutputBytes` (256 KiB); killed |
| `CUSTOM_RULE_INPUT_TOO_LARGE` | request above `MaxInputBytes` (4 MiB); runner not started |
| `CUSTOM_RULE_MEMORY_LIMIT` | working set above `MaxMemoryMegabytes` (+96 MB runtime allowance); killed. The child also gets `DOTNET_GCHeapHardLimit`. |
| `CUSTOM_RULE_STEP_BUDGET_EXCEEDED`, `CUSTOM_RULE_REGEX_TIMEOUT` | in-process guards (50M step budget, 2 s per regex match) |
| `CUSTOM_RULE_CRASH`, `CUSTOM_RULE_MALFORMED_OUTPUT`, `CUSTOM_RULE_LAUNCH_FAILED`, `CUSTOM_RULE_BAD_REQUEST` | abnormal exit, invalid result, cannot start, bad request |
| `CUSTOM_RULE_INVALID_RULE` / `_INVALID_PARAMETERS` | reported as a misconfigured binding |

The child environment is scrubbed (no inherited credentials); stderr is discarded.

## Network deny

The runner contains no networking code, but an OS process shares the parent's network namespace, so enforce
it at the platform level. Options: wrap `Command` (for example `unshare -rn dotnet Axiom.PolicyRunner.dll`, or a
container runtime with `--network none`), or run the runner in its own pod with a deny-all policy:

```yaml
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata: { name: axiom-policy-runner-deny-all }
spec:
  podSelector: { matchLabels: { app.kubernetes.io/component: policy-runner } }
  policyTypes: [Ingress, Egress]
```

with `runAsNonRoot`, `readOnlyRootFilesystem`, `capabilities.drop: [ALL]`, `automountServiceAccountToken: false`.
The Helm/Kustomize manifests are not changed by this work.

## Configuration

```
Axiom:Policy:CustomRunner:Enabled = true
Axiom:Policy:CustomRunner:Rules:0:Name = no-todo
Axiom:Policy:CustomRunner:Rules:0:Version = 1.0.0
Axiom:Policy:CustomRunner:Rules:0:Spec = { ...definition JSON... }
```
