# Deterministic resolver benchmark (task 3.10)

Harness: `tests/Axiom.Benchmarks` (xUnit, `Stopwatch`, no extra packages). Tests carry `[Trait("Category", "Benchmark")]`.

```
dotnet test tests/Axiom.Benchmarks --logger "console;verbosity=detailed"   # prints p50/p95/p99
dotnet test Axiom.slnx --filter "Category!=Benchmark"                      # skip it in quick runs
```

The project is not referenced by `Axiom.UnitTests`, so the normal unit-test run never executes it. It runs in about 30 s.

## Corpus and target

Seeded (`Random(20261018)`) synthetic corpus at the reference scale of `requirements.md` section 4: 25,000 governance records
including history (about 20% accepted, the rest superseded, deprecated, rejected or proposed; organization, domain, system, component,
repository and path scopes; refinement relations), 1,000 repositories, 10,000 components, 100 systems, 100 concurrent agent sessions.
Each sample resolves one random change scope with `DecisionResolver.Resolve` over all 25,000 records.

Target: p95 deterministic preflight under 1.5 s at normal load. The resolver is one step of preflight, so asserting it
against the whole budget is the loosest honest bound; the tables show the headroom left for the other steps.

## Measured (16-core Windows dev machine, Debug build, Server GC; numbers vary run to run)

| Scenario | n | p50 | p95 | p99 | Assertion |
|---|---|---|---|---|---|
| Sequential, 25,000 records | 100 | 149 ms | 188 ms | 237 ms | p95 < 1.5 s (pass) |
| 4 resolutions in flight (normal load) | 32 | 321 ms | 424 ms | 439 ms | p95 < 1.5 s (pass) |
| Burst: 100 sessions at once, one replica | 100 | 1,351 ms | 1,631 ms | 1,788 ms | p95 < 5 s (pass) |

Earlier runs on the same machine gave sequential p95 between 188 and 368 ms and a burst p95 between 1.6 and 2.3 s.

## Findings

- Sequential resolution uses 10-25% of the 1.5 s budget, so the target holds at the reference scale.
- Resolution is O(records) per call and allocation heavy (it scans, sorts and traces every record), and parallel speedup is poor (about 2x on 16 cores).
  A burst of all 100 sessions resolving at the same instant on one replica exceeds the 1.5 s normal-load target (it stays under 5 s). 10,000 evaluations a day
  is about 0.1 per second, so this is a worst case; run at least two API replicas, or prefilter records by scope index before resolving if bursts matter.
- Server GC with concurrent GC disabled is set for this project because the default workstation GC gave no parallel speedup at all (burst p95 4.5 s).
- Correctness is also covered: the result is identical for the same input, and independent of record order (ordered, reversed, shuffled).
