# TaskForge benchmark v2 results

This file records the controlled TaskForge v2 benchmark defined in `docs/benchmark-v2.md`.

Prompt artifact: `docs/benchmarks/taskforge-prompt-v1.txt`  
Prompt SHA-256: `1b1fd2bc6257cecf2a60fda1b579fb9f4f742d16dfdd299fa12070ab0b2e1d79`  
Prompt encoding: UTF-8  
Prompt length: 1519 bytes  
Prompt final newline: no

The prompt artifact is the benchmark input. Runs must read/use those exact bytes; do not copy, retype, normalize, or edit the prompt between arms.

## Frozen paired-run invariants

For every paired A/B comparison, keep identical:

- LoopGolem commit
- TaskForge base commit
- Codex CLI version
- host OS and agent runtime / WSL distribution
- Planner/Supervisor model and reasoning policy
- Worker model and reasoning policy
- Validator model and reasoning policy
- task granularity
- recovery and validation policies
- prompt SHA-256
- external acceptance/smoke procedure
- no human intervention during a run

Arm A must use `SessionReuseMode.Disabled` and show `Worker context: Fresh per task`.

Arm B must use `SessionReuseMode.Affinity` and show `Worker context: Affinity`.

Preferred order when quota permits: A1 -> B1, then B2 -> A2.

## First pair — 2026-09-25

Shared run data:

```text
LoopGolem commit: cdd19c34a425d993663202e37d8b0d13d44bd37a
TaskForge base commit: f9dce011f2f9a4f65c93621584ce136d2f5a3aed
Codex CLI version: 0.156.1
Host OS: Windows (exact build not recorded)
Agent runtime / WSL distribution: WSL used by Codex; distribution not recorded
Prompt SHA-256: 1b1fd2bc6257cecf2a60fda1b579fb9f4f742d16dfdd299fa12070ab0b2e1d79
Human intervention during mission execution: no
```

Both missions ended in `NeedsHumanAttention` before the independent Validator, so both are formal benchmark failures under the quality criteria even though both final workspaces later proved usable. Post-run diagnostic commands were executed only after each mission had reached its terminal state and do not count as intervention during the run.

### Run A1

```text
Arm: A — Fresh Worker control
Order / repetition: A1
Observed Worker context: Fresh per task
Mission status: NeedsHumanAttention
Elapsed time: 12:00
Input tokens: 1,126,250
Cached input tokens: 834,560
Cache-write input tokens: 0
Output tokens: 49,274
Reasoning output tokens: 15,230
Comparable total: 1,175,524
Sessions: 10
Turns: 13
Worker resumed turns: 0
Worker reuse hints: 1
Recovery cycles: 3
Successful recovery cycles: 0
Exhausted recovery cycles: 1
Supervisor turns / tokens: 4 / 482,901
Worker turns / tokens: 9 / 692,623
Validator turns / tokens: 0 / 0
External acceptance/smoke tests: PASS
Post-run build: PASS
Post-run intended self-test: PASS when supplied the required TaskForge.Cli.dll argument
Human intervention during mission execution: no
```

A1 reached its planned deterministic self-test, but the generated self-test executable required the path to `TaskForge.Cli.dll` while the planned deterministic invocation omitted that argument. The failed process evidence was persisted and recovery was entered. Recovery added the CLI and self-test projects to the solution and then exhausted three cycles without reconciling the invocation mismatch. After the terminal mission state, the generated solution built successfully and the self-test passed when called with the required DLL argument.

The final A1 artifact also passed an independent external smoke covering add, persistence across processes, list/show, tag/untag, complete/reopen, search, list filters, delete, monotonic/non-reused IDs, invalid IDs, invalid commands, and valid JSON persistence.

### Run B1

```text
Arm: B — Affinity
Order / repetition: B1
Observed Worker context: Affinity
Mission status: NeedsHumanAttention
Elapsed time: 19:41
Input tokens: 1,368,418
Cached input tokens: 1,074,176
Cache-write input tokens: 0
Output tokens: 57,259
Reasoning output tokens: 17,302
Comparable total: 1,425,677
Sessions: 8
Turns: 13
Worker resumed turns: 2
Worker reuse hints: 3
Recovery cycles: 3
Successful recovery cycles: 0
Exhausted recovery cycles: 1
Supervisor turns / tokens: 4 / 521,710
Worker turns / tokens: 9 / 903,967
Validator turns / tokens: 0 / 0
External acceptance/smoke tests: PASS
Post-run build: PASS
Post-run intended self-test: PASS
Human intervention during mission execution: no
```

B1 exercised Affinity for real: two Worker turns resumed prior Worker sessions and three Worker turns recommended reuse. The mission nevertheless reached a failing deterministic self-test and exhausted three recovery cycles before validation. Recovery generated source repairs for task JSON deserialization. The failed self-test was a `--no-build` check, so rerunning only that exact check after source repair could test stale build artifacts. After the terminal mission state, a manual deterministic build followed by the same intended self-test passed.

The final B1 artifact also passed the same independent external smoke as A1. B1 used `TASKFORGE_DATA_DIR` for test-store isolation while A1 used `TASKFORGE_DATA_PATH`; the smoke harness accounted for both generated designs.

## First-pair comparison

| Metric | A1 Fresh | B1 Affinity |
| --- | ---: | ---: |
| Mission status | NeedsHumanAttention | NeedsHumanAttention |
| Elapsed | 12:00 | 19:41 |
| Input | 1,126,250 | 1,368,418 |
| Cached input | 834,560 | 1,074,176 |
| Non-cached input (derived) | 291,690 | 294,242 |
| Cache-write input | 0 | 0 |
| Output | 49,274 | 57,259 |
| Reasoning output | 15,230 | 17,302 |
| Comparable total | 1,175,524 | 1,425,677 |
| Sessions | 10 | 8 |
| Turns | 13 | 13 |
| Worker resumed turns | 0 | 2 |
| Worker reuse hints | 1 | 3 |
| Recovery cycles | 3 | 3 |
| Successful recovery cycles | 0 | 0 |
| Exhausted recovery cycles | 1 | 1 |
| Supervisor tokens | 482,901 | 521,710 |
| Worker tokens | 692,623 | 903,967 |
| Validator tokens | 0 | 0 |
| External smoke | PASS | PASS |

The first pair does not establish whether Affinity is beneficial. Affinity was active and reduced the number of Worker sessions, but B1 used more raw tokens and more elapsed time while both runs formally failed before validation. The two runs also diverged materially in generated architecture and failure mode, so model/planning variance is a major confounder.

Notably, derived non-cached input was nearly equal (291,690 for A1 versus 294,242 for B1). Most of B1's larger raw input total was cached input. Effective monetary cost must therefore not be inferred from comparable totals alone.

The benchmark also exposed a recovery correctness concern independently of the Affinity question: a source repair can invalidate artifacts created by an earlier deterministic prerequisite, while current recovery reruns only the originally failed deterministic check. A `--no-build` check can therefore observe stale binaries. This should be fixed and covered by deterministic recovery tests before spending quota on B2/A2 with this implementation.

## Run B2

Deferred until the recovery prerequisite-invalidation issue discovered in the first pair is corrected and validated.

## Run A2

Deferred until the recovery prerequisite-invalidation issue discovered in the first pair is corrected and validated.

## Analysis discipline

Compare input, cached input, cache-write input, output, and reasoning output separately. Do not infer effective cost from raw/comparable token totals alone.

Compare completion status, external acceptance/smoke quality, recovery behavior, validation corrections, session/turn behavior, and elapsed time alongside token dimensions.

Historical TaskForge measurements are context only and are not causal controls for this A/B.

Change only one benchmark variable at a time. Implementation defects discovered by the benchmark should be corrected and validated before beginning a new controlled pair.
