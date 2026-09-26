# TaskForge benchmark v3 — first controlled allowance run

Date: 2026-09-26

This document records the first execution of the benchmark defined in
`docs/benchmark-v3-allowance-strategy.md`.

The run is **inconclusive as a strategy comparison**. Arm F failed before any
model turn, while Arm H completed the LoopGolem mission. Its original external
acceptance capture aborted on a Windows PowerShell 5.1 stderr-handling bug, but
the preserved H artifact was rerun through the corrected harness with **PASS**
and no additional model inference. No product-policy winner should be selected
from this run because Arm F never executed a model turn.

The run is still useful because it exposed two benchmark/runtime defects and
produced a clean allowance/token observation for the Fresh + High arm.

## Frozen inputs

LoopGolem commit under test:

```text
da3cdeaa8cb6c1172e80a7e5f17330a83c47fbbc
```

Target base commit:

```text
5bb02f3336c8031973b3f15c3346d39c505ad236
```

Canonical TaskForge goal SHA-256:

```text
1b1fd2bc6257cecf2a60fda1b579fb9f4f742d16dfdd299fa12070ab0b2e1d79
```

Frozen PlannerResult SHA-256:

```text
69f3df8fbb64fd815e66b8c8c62bc55af1774efa17b2b72afd9db0f9f11b0d21
```

Codex CLI:

```text
codex-cli 0.156.1
Logged in using ChatGPT
```

The deterministic preflight, Release build and Worker self-test passed before
the benchmark consumed any model allowance.

## Results

| Dimension | Plan-only | Arm F — SupervisorFork + Low | Arm H — Fresh + High |
| --- | ---: | ---: | ---: |
| Mission status | Paused after planning | Failed | Completed |
| Elapsed seconds | — | 10.751 | 587.758 |
| Input tokens | 13,709 | 0 | 981,517 |
| Cached input tokens | 0 | 0 | 640,000 |
| Cache-write input tokens | 0 | 0 | 0 |
| Output tokens | 4,700 | 0 | 23,803 |
| Reasoning output tokens | 1,303 | 0 | 9,539 |
| Total tokens | 18,409 | 0 | 1,005,320 |
| Programmatic 5h used | — | 0% -> 0% | 0% -> 1% |
| Visible UI remaining | — | 100% -> 100% | 100% -> 99% |
| External acceptance | not applicable | not reached meaningfully | PASS after harness fix, no new inference |
| Functional eligibility | not applicable | no | yes |

Arm H role telemetry:

| Role | Sessions | Turns | Input | Cached input | Output | Reasoning | Total |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Supervisor | 1 | 1 | 151,142 | 0 | 605 | 342 | 151,747 |
| Worker | 8 | 8 | 754,355 | 597,504 | 21,677 | 8,060 | 776,032 |
| Validator | 1 | 1 | 76,020 | 42,496 | 1,521 | 1,137 | 77,541 |

The role totals sum to the Arm H mission total of 1,005,320 tokens.

The plan-only mission plus Arm H reported 1,023,729 total tokens. Arm F
reported zero model tokens because it stopped before its first Worker turn.

## Arm F failure — reasoning-effort fork invariant

Arm F executed the deterministic setup tasks and then stopped at the first
Worker task:

```text
Forked Worker did not inherit HIGH reasoning; observed '(null)'.
```

This occurred before `turn/start`, so the run does **not** measure the cost or
quality of SupervisorFork + Low.

The fork transport checks the returned model before it checks inherited
reasoning effort. The model mismatch guard did not fire; execution reached the
reasoning guard. Therefore this specific `thread/fork` response matched the
requested `gpt-6-luna` model, but returned a null `reasoningEffort`.

The corrective direction is to configure the fork child explicitly with
`model_reasoning_effort="high"` instead of assuming that HIGH is inherited
implicitly. The transport should keep both post-fork invariants:

1. returned model must equal the requested model;
2. returned fork effort must be HIGH before the Worker LOW `turn/start`.

The first post-run implementation change now supplies explicit HIGH fork
configuration and also forces workspace-write network access off.

## Model pinning and the historical Astra hypothesis

Before the pre-benchmark fix, LoopGolem did not send `model` in the
`thread/fork` request. Earlier probes verified the parent thread as
`gpt-6-luna`, but did not consistently verify the child fork model.

This run does **not** prove that historical children silently became GPT-6
Astra. It does show that explicit model pinning is now enforced and that the
measured fork reached the later reasoning-effort guard rather than the model
guard.

A no-turn app-server probe is now versioned at:

`docs/benchmarks/probe-fork-model-no-turn.py`

It creates one Luna HIGH parent thread without starting a model turn and
compares:

- an old-style `thread/fork` request with no explicit model;
- a pinned `thread/fork` request with `model="gpt-6-luna"`.

The probe intentionally never calls `turn/start`. Its purpose is model
identity diagnosis, not cost measurement.

## Arm H allowance observation

Arm H performed substantial useful model work:

- 1 Supervisor turn;
- 8 fresh HIGH Worker turns;
- 1 Validator turn;
- 1,005,320 total reported mission tokens;
- 640,000 cached input tokens.

Across the measured arm, both allowance views moved by one visible point:

```text
account/rateLimits/read: 0% used -> 1% used
visible UI:            100% remaining -> 99% remaining
```

This is a useful sanity check that the benchmark can observe an allowance
change. It does not define a general conversion from tokens to allowance:
the five-hour meter is integer-valued and may be delayed, quantized or
coalesced.

Because Arm F made no model turn, this run cannot compare the allowance
economics of Fork/Low against Fresh/High.

## Arm H external acceptance — harness defect

Arm H reached `Completed` and its generated TaskForge Release build succeeded
with zero warnings and zero errors during external acceptance.

The acceptance harness then deliberately exercised an invalid ID. TaskForge
returned the intended error:

```text
Error: Task 999999 does not exist.
```

On Windows PowerShell 5.1, native stderr redirected through the pipeline was
surfaced as a `NativeCommandError`. Because the harness used
`$ErrorActionPreference = "Stop"`, PowerShell aborted the harness before it
could inspect the expected non-zero native exit code.

Therefore `externalAcceptance=false` in the original capture was not evidence
that the invalid-ID behavior was wrong.

The harness was changed to capture native stderr under a temporary `Continue`
preference and then evaluate `$LASTEXITCODE` explicitly. The preserved H
workspace was rerun through that corrected harness without any new model
inference. The result was:

```text
TaskForge external acceptance: PASS
```

Arm H is therefore functionally eligible for this run: LoopGolem mission
`Completed`, internal Validator accepted the snapshot, deterministic Release
build passed, and the corrected independent external acceptance suite passed.
This does not create a strategy winner because Arm F never executed a model
turn and cannot be compared economically or functionally.

## Interpretation

Do not select a benchmark winner from this run.

What the run establishes:

1. the frozen-plan/reset/allowance benchmark machinery works end-to-end;
2. explicit model pinning on `thread/fork` passed for the attempted F child;
3. implicit HIGH inheritance is not a valid production invariant for this
   fork shape because the response reported null effort;
4. Fresh + High can complete the mission with the frozen plan;
5. roughly one million reported Arm H tokens coincided with one visible
   percentage point of five-hour allowance in this isolated run;
6. the PowerShell 5.1 acceptance harness needs special native-stderr handling;
7. after that harness fix, the preserved Arm H artifact passes the full external acceptance suite without additional model inference.

What the run does **not** establish:

- that SupervisorFork + Low is cheaper;
- that SupervisorFork + Low is functionally viable;
- that historical unpinned forks used Astra;
- that one million Luna tokens always cost one five-hour percentage point;

## Next steps

1. run the no-turn old-style-vs-pinned fork model identity probe;
2. validate the explicit-HIGH fork fix with deterministic CI/self-tests;
3. run a narrow F-only smoke/measurement before paying for another full F/H
   pair;
4. only after F executes real Worker turns, repeat the paired strategy
   benchmark with the same frozen-input controls.
