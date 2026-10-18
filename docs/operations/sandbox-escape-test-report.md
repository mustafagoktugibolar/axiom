# Sandbox escape test report (plan item 12.3)

Scope: the custom policy runner (`docs/custom-policy-runner.md`): rule language, protocol, `Axiom.PolicyRunner`
process and `ProcessCustomRunnerProcess` supervisor. Suite: `tests/Axiom.UnitTests/Policy/Custom/SandboxEscapeTests.cs`
(about 140 test cases, no Docker; some tests start `dotnet`/`powershell`/`sh` locally). Run with
`dotnet test tests/Axiom.UnitTests --filter FullyQualifiedName~SandboxEscapeTests`.

Pass criterion for every attack: `PolicyOutcome.Error` with a stable `CUSTOM_RULE_*` reason (or a fail-closed
`Violated`), or a bounded correct result. Never a wrong `Passed`, never a hang, never an unhandled exception.

## Coverage

| Attack | Result |
|---|---|
| Lookahead, lookbehind, backreference, atomic group, conditional, right-to-left, malformed patterns | Rejected as `CUSTOM_RULE_INVALID_RULE` (NonBacktracking engine); binding is an engine Error, no violations |
| Catastrophic backtracking (`(a+)+$`, `(a\|aa)+$`, `(.*)*x`, ...) on 200 KB lines | Linear time, bounded |
| Huge repetition (`a{1000000}`, `((a{1000}){1000}){1000}`, `{1,2147483647}`) in a real runner process | Terminates well inside the timeout; result is a pass/violation/error, no host impact |
| Parameter value used to grow a pattern past the 1000-character cap | **Defect, fixed** (see below) |
| Placeholder tricks (`{{`, `{{}}`, nested, re-expansion of values) | Undeclared names are `INVALID_RULE`; values are never re-expanded |
| Oversized (64 KB) / deeply nested definitions, unknown properties or check types, 21 checks | `INVALID_RULE` |
| Duplicate JSON keys in a definition or request | **Defect, fixed**: now `INVALID_RULE` / `BAD_REQUEST` |
| Unsupported json-path syntax (`$..a`, filters, slices, negative or overflowing index, 33+ segments), smuggled via a parameter | `INVALID_RULE` |
| 100000-deep JSON content; wide arrays with 32 `[*]` segments | Deep JSON is a violation (never a pass); wildcard chain stops at the step budget (`CUSTOM_RULE_STEP_BUDGET_EXCEEDED`) |
| Step budget on large content | `STEP_BUDGET_EXCEEDED` |
| Violation flood (3000 files x 3 lines x 5 checks, 2000 matching lines) | At most 500 violations, order-independent; engine adds an omission notice |
| Over-long violation message | Truncated to 2000 characters |
| Giant input (6 files x 900 KB) | `INPUT_TOO_LARGE`, runner not started |
| Real runner given more stdin than its cap | Exit code 3, no output |
| Result above the output cap in the real runner | `OUTPUT_TOO_LARGE` error |
| Traversal and absolute selectors (`../../etc/passwd`, `C:\Windows\win.ini`, UNC) in `require-file` / `select.paths` | Only matched against the tree path list; the runner never opens files. Missing = violation |
| `..` segments or control characters (NUL) in change or tree paths | **Defect, fixed**: `BAD_REQUEST` |
| Separator and dot tricks (`secrets\key`, `./secrets//key`, whitespace) | Still match the selector |
| Degenerate selectors (`" "`, `.`, `/`, `./`) | **Defect, fixed**: `INVALID_RULE` instead of an unclassified crash |
| Pathological globs | Terminate |
| Rule ids: case, backslash, whitespace, zero-width, full-width, `custom/./`, built-in names under `custom/` | Resolve to nothing: engine Error (unknown rule); a custom rule cannot replace a built-in or register twice |
| Configured rule names with non-ASCII, `/`, spaces, empty | Refused at startup (`InvalidOperationException`); runner off by default registers nothing |
| Truncated request at every byte offset, 1500 random single-byte corruptions, BOM, invalid UTF-8, deep nesting, nulls inside collections, bad line numbers, trailing data, two documents | Always a well-formed error response (`BAD_REQUEST`/`INVALID_RULE`), core never throws. Nulls inside collections and non-positive line numbers were **defects, fixed** |
| Truncated or tampered runner response (every offset, two documents, bad enum case, null violations, oversized fields, 501 violations) | `MALFORMED_OUTPUT` |
| Runner that claims "passed" with non-zero exit, empty output, or after a timeout/overflow/memory status | Error |
| Runner error text that quotes repository content | Only the stable code is recorded |
| Environment leakage (planted connection string, API keys, tokens, `KUBERNETES_*`, `Axiom__*`) | Child sees none of them or their values |
| Child working directory | The temp directory, not the application directory |
| Child stderr | Discarded |
| Child that ignores stdin and spins | Killed at timeout |
| Grandchild process left behind by a timed-out child | Killed with the tree (Windows run verified; the `sh` variant is written for Linux CI) |
| Memory hog | Killed with `MemoryLimitExceeded` (Windows only test) |

## Defects found and fixed

1. **Parameter substitution bypassed the field caps.** Caps were checked on the definition before binding, so a
   100 KB parameter value could produce a huge pattern, glob or message in the runner. `CustomRuleSpec.Bind` now
   re-checks every substituted field (`CUSTOM_RULE_INVALID_PARAMETERS`); the parent's pre-bind catches it before launch.
2. **Degenerate globs crashed the runner with an unclassified exit.** `GlobPattern.Parse` threw `ArgumentException`
   for `" "`, `.`, `/`; the runner exited 70 and the parent reported `CUSTOM_RULE_CRASH`. Now `INVALID_RULE`.
3. **`..` and control characters in paths** let a name such as `src/../secrets/key` miss a `secrets/**` selector
   while naming the same file. Real repositories cannot contain such paths, so the request is now `BAD_REQUEST`.
4. **Nulls inside request collections and line numbers below 1** raised a `NullReferenceException` or produced a
   response the parent then rejected. The request is validated up front (`BAD_REQUEST`), and `CustomRuleRunnerCore`
   converts any other unexpected exception into `CUSTOM_RULE_CRASH` instead of skipping the protocol.
5. **Duplicate JSON keys** were accepted (last one wins), so a definition could read differently to a human than to
   the runner. Duplicates are now rejected in definitions and requests (`AllowDuplicateProperties = false`).

## Gaps that code cannot close

- **No OS memory cap.** The child gets `DOTNET_GCHeapHardLimit` and the supervisor polls the working set every 20 ms,
  so a fast allocation burst can overshoot before the kill. Real caps need cgroups / container limits on the pod.
- **Network deny only at deployment level.** The runner has no networking code, but an OS child shares the parent's
  network namespace. Needs the NetworkPolicy or `unshare -rn` wrapper from `custom-policy-runner.md`; none ships in `deploy/`.
- **No filesystem isolation.** The child runs as the same user. A bug in the runtime could read what the parent can.
  Mitigation is container hardening (non-root, read-only root, dropped capabilities).
- **Regex construction is not covered by the match timeout.** Pattern compilation of huge repetitions is bounded only
  by the wall-clock and memory supervision. Observed: fast failure or fast success for all tested patterns, but this
  is an empirical result, not a guarantee.
- **Unicode normalization.** Paths are compared ordinally. A decomposed (NFD) spelling of a name does not match a
  selector written in composed (NFC) form, and look-alike or invisible characters are distinct names. The repository
  runs with `InvariantGlobalization`, where `string.Normalize` is a no-op, so this cannot be fixed with the framework call; it needs a managed
  normalizer. Case is also significant (`SECRETS/` does not match `secrets/**`), which matters for case-insensitive
  filesystems.
- **Violation flood can become an error.** 500 long violations can exceed the 256 KiB output cap; the result is then
  `OUTPUT_TOO_LARGE` (fail closed) rather than a truncated violation list.
- **Linux process-tree and memory tests not run here.** The suite ran on Windows; the `sh` branches of the
  tree-kill and environment tests need a Linux CI run.
- **Resource limits are per call.** Many concurrent evaluations multiply runner processes; there is no global
  concurrency cap on runner launches.
