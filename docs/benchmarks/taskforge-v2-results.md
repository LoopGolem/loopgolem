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

## Run A1

```text
Arm: A — Fresh Worker control
Order / repetition: A1
LoopGolem commit:
TaskForge base commit:
Codex CLI version:
Host OS:
Agent runtime / WSL distribution:
Prompt SHA-256: 1b1fd2bc6257cecf2a60fda1b579fb9f4f742d16dfdd299fa12070ab0b2e1d79
Observed Worker context:
Mission status:
Elapsed time:
Input tokens:
Cached input tokens:
Cache-write input tokens:
Output tokens:
Reasoning output tokens:
Comparable total:
Sessions:
Turns:
Worker resumed turns:
Worker reuse hints:
Recovery cycles:
Successful recovery cycles:
Exhausted recovery cycles:
Supervisor turns / tokens:
Worker turns / tokens:
Validator turns / tokens:
External acceptance/smoke tests:
Human intervention: no
Notes:
```

## Run B1

```text
Arm: B — Affinity
Order / repetition: B1
LoopGolem commit:
TaskForge base commit:
Codex CLI version:
Host OS:
Agent runtime / WSL distribution:
Prompt SHA-256: 1b1fd2bc6257cecf2a60fda1b579fb9f4f742d16dfdd299fa12070ab0b2e1d79
Observed Worker context:
Mission status:
Elapsed time:
Input tokens:
Cached input tokens:
Cache-write input tokens:
Output tokens:
Reasoning output tokens:
Comparable total:
Sessions:
Turns:
Worker resumed turns:
Worker reuse hints:
Recovery cycles:
Successful recovery cycles:
Exhausted recovery cycles:
Supervisor turns / tokens:
Worker turns / tokens:
Validator turns / tokens:
External acceptance/smoke tests:
Human intervention: no
Notes:
```

## Run B2

```text
Arm: B — Affinity
Order / repetition: B2
LoopGolem commit:
TaskForge base commit:
Codex CLI version:
Host OS:
Agent runtime / WSL distribution:
Prompt SHA-256: 1b1fd2bc6257cecf2a60fda1b579fb9f4f742d16dfdd299fa12070ab0b2e1d79
Observed Worker context:
Mission status:
Elapsed time:
Input tokens:
Cached input tokens:
Cache-write input tokens:
Output tokens:
Reasoning output tokens:
Comparable total:
Sessions:
Turns:
Worker resumed turns:
Worker reuse hints:
Recovery cycles:
Successful recovery cycles:
Exhausted recovery cycles:
Supervisor turns / tokens:
Worker turns / tokens:
Validator turns / tokens:
External acceptance/smoke tests:
Human intervention: no
Notes:
```

## Run A2

```text
Arm: A — Fresh Worker control
Order / repetition: A2
LoopGolem commit:
TaskForge base commit:
Codex CLI version:
Host OS:
Agent runtime / WSL distribution:
Prompt SHA-256: 1b1fd2bc6257cecf2a60fda1b579fb9f4f742d16dfdd299fa12070ab0b2e1d79
Observed Worker context:
Mission status:
Elapsed time:
Input tokens:
Cached input tokens:
Cache-write input tokens:
Output tokens:
Reasoning output tokens:
Comparable total:
Sessions:
Turns:
Worker resumed turns:
Worker reuse hints:
Recovery cycles:
Successful recovery cycles:
Exhausted recovery cycles:
Supervisor turns / tokens:
Worker turns / tokens:
Validator turns / tokens:
External acceptance/smoke tests:
Human intervention: no
Notes:
```

## Analysis discipline

Compare input, cached input, cache-write input, output, and reasoning output separately. Do not infer effective cost from raw/comparable token totals alone.

Compare completion status, external acceptance/smoke quality, recovery behavior, validation corrections, session/turn behavior, and elapsed time alongside token dimensions.

Historical TaskForge measurements are context only and are not causal controls for this A/B.

Change only one variable in the experiment that follows this benchmark.
