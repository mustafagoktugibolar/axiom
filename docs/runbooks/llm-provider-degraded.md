# LLM provider degraded

Semantic analysis is advisory and never produces BLOCK. Provider failure degrades coverage; the deterministic verdict is unchanged. Off by default
(`Axiom:Semantic:Enabled`); provider is `Heuristic` (local) or `Anthropic` (`Axiom:Semantic:Provider`).

## Symptoms

- Evaluation results carry semantic coverage `Degraded` with the reason `The semantic provider timed out.` or `The semantic provider failed or returned an invalid answer.`
- Latency: alerts `AxiomSemanticPreflightLatencySloBreach` (stage `Diff`, p95 > 5 s) and `AxiomPullRequestLatencySloBreach` (stage `PullRequest`, p95 > 30 s). Semantic analysis can add up to `Axiom:Semantic:TimeoutSeconds` (default 20).
- `axiom_semantic_findings_total` drops to zero while `axiom_evaluations_total` continues; `axiom_semantic_tokens_total` drops or spikes.
- Quality drift instead of outage: alert `AxiomSemanticFalsePositiveRateHigh`; see `GET /v1/semantic/metrics?from=&to=` and the gauge `axiom_semantic_overturn_rate`.
- Coverage `ProhibitedByScope` is not a degradation: a record forbids external models for that scope.

## Diagnosis

1. Confirm the verdict path is healthy: `axiom_evaluations_total{verdict}` continues and `axiom_receipt_failures_total` is flat. A degraded provider must not change verdicts.
2. Provider side: status page of the provider, API key validity (a 401 appears as "failed"), rate limiting, network egress from the API pods to the provider.
3. Deployment side: `kubectl -n axiom exec deploy/axiom-api -- printenv | grep Axiom__Semantic` shows configuration without secrets; `kubectl -n axiom logs deploy/axiom-api --since=30m | grep -i semantic`.
4. Timeouts: compare `axiom_evaluation_duration_milliseconds` for `Diff` with the configured timeout. If p95 approaches the timeout, the provider is slow rather than down.

## Mitigation

- Wait, if the deterministic gate is intact; reviewers see the `Degraded` coverage note and review manually.
- Reduce exposure: lower `Axiom:Semantic:TimeoutSeconds` so slow providers cost less latency, or set `Axiom__Semantic__Provider=Heuristic` (no data leaves the deployment) and roll
  the API (`kubectl -n axiom rollout restart deploy/axiom-api`).
- Key problem: rotate the key per secret-rotation.md.
- Switching the provider off (`Axiom__Semantic__Enabled=false`) removes semantic coverage entirely; announce it, because reviewers lose the signal.

## Escalate

Escalate to the platform owner when the deterministic verdict path is affected (evaluation errors, receipt failures) or when degradation persists past the provider's own incident.

## Do not

- Do not treat missing semantic findings as "no issues"; coverage `Degraded` means unchecked.
- Do not point the provider at an unvetted endpoint or send scoped-out content to an external model to get coverage back.
- Do not lower `MinConfidence`/`ReviewConfidence` to recover finding volume; calibrate from `/v1/semantic/metrics`.
