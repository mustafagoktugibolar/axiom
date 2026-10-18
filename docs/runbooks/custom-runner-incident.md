# Custom runner incident

Custom policy rules (`custom/<name>`) are declarative JSON interpreted by the separate `Axiom.PolicyRunner` process (`docs/custom-policy-runner.md`). Off by default
(`Axiom:Policy:CustomRunner:Enabled`). A runner failure is always a policy `Error` for that rule, never a pass. Test evidence: `docs/operations/sandbox-escape-test-report.md`.

## Symptoms

- Findings with code `POLICY_RULE_ERROR` and text `Rule 'custom/<name>' failed with CUSTOM_RULE_<CODE>.` (severity at least RequireReview), or `POLICY_RULE_MISCONFIGURED` for an invalid definition or parameters.
- Stable reason codes: `CUSTOM_RULE_TIMEOUT`, `OUTPUT_TOO_LARGE`, `INPUT_TOO_LARGE`, `MEMORY_LIMIT`, `STEP_BUDGET_EXCEEDED`, `REGEX_TIMEOUT`, `CRASH`, `MALFORMED_OUTPUT`, `LAUNCH_FAILED`, `BAD_REQUEST`.
- Evaluation latency alerts (`AxiomPreflightLatencySloBreach`, `AxiomPullRequestLatencySloBreach`) if rules hit their timeout (default 10 s each).
- Node CPU/memory pressure or many `dotnet` child processes in the API pod. There is no runner-specific metric or alert yet (listed as a gap in `axiom-alerts.yaml`).

## Diagnosis

1. Which rule and which code: the finding text above, or `GET /v1/evaluations/{id}` for the failing evaluation. The error contains only the code, never repository content.
2. By code:
   - `LAUNCH_FAILED`: the runner command or `Axiom.PolicyRunner.dll` is missing from the image; check `Axiom__Policy__CustomRunner__Command/Arguments` and the image.
   - `CRASH` / `MALFORMED_OUTPUT`: runner bug or incompatible version; compare image versions and protocol version; try the same definition locally with the unit-test harness.
   - `TIMEOUT`, `MEMORY_LIMIT`, `STEP_BUDGET_EXCEEDED`, `REGEX_TIMEOUT`: the rule or the change is too expensive. Look at the definition (`Axiom:Policy:CustomRunner:Rules:N:Spec`) for broad regex over huge content or long `[*]` json-paths.
   - `INPUT_TOO_LARGE`/`OUTPUT_TOO_LARGE`: very large change or a rule that matches thousands of lines.
3. Containment check: `kubectl -n axiom exec deploy/axiom-api -- ps aux | grep -i PolicyRunner` for runaway processes (the supervisor kills the process tree on timeout, so lingering processes are abnormal).
4. If you suspect an escape attempt (unexpected network or file activity from the pod), preserve pod logs and node telemetry before restarting anything.

## Mitigation

- Isolate the rule: remove or fix that rule's entry in the configuration, then `kubectl -n axiom rollout restart deploy/axiom-api`. Each rule is independent; other rules and built-in rules keep running.
- Disable everything custom: `Axiom__Policy__CustomRunner__Enabled=false` and restart. No custom rule is registered then, and bindings to `custom/*` become unknown-rule errors (still fail-safe, review required).
- Tighten limits for the future: `TimeoutSeconds`, `MaxMemoryMegabytes`, `MaxInputBytes`, `MaxOutputBytes` under the same section.
- Containment is only as strong as the deployment: confirm the pod has a deny-all NetworkPolicy, non-root, read-only root filesystem and memory limits (known gaps in the report: no OS memory cap, no network deny in code).

## Escalate

Security owner immediately for a suspected escape or data exposure. Platform owner for crashes after an image upgrade. Include the rule name, reason code, definition hash (the version suffix `+spec.<hash>`), and affected evaluation ids.

## Do not

- Do not treat `POLICY_RULE_ERROR` as a pass or waive it (errors are never waived); reviewers confirm compliance manually until the rule works.
- Do not raise timeouts or memory caps in order to make a hostile or runaway rule "work".
- Do not load rule definitions from repositories or add executable/assembly paths to the runner command; definitions are data only.
- Do not run the runner outside the isolated pod/process boundary, for example inside the Workers process with extra privileges.
