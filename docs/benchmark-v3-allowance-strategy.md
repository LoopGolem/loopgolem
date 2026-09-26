# Benchmark v3 — Codex allowance strategy

This benchmark compares two complete Worker execution strategies for the same frozen mission plan.

It is intentionally a **strategy benchmark**, not a single-variable causal experiment. The two arms differ in both context lineage and Worker reasoning effort because they represent the two product policies LoopGolem may expose to users.

## Question

For the same PlannerResult and target workspace, which Worker strategy produces more useful completed work per point of the included Codex five-hour allowance?

The primary cost metric is the account-level five-hour allowance movement observed before and after each controlled arm.

Raw token dimensions remain mandatory diagnostic telemetry, but they are not assumed to map linearly to the included-plan allowance.

## Arms

### F — Supervisor fork / Low

- Planner/Supervisor: GPT-6 Luna High.
- Worker context: `WorkerContextStrategy.SupervisorFork`.
- Worker reasoning: `WorkerReasoningEffort.Low`.
- Each Worker forks from the persisted Supervisor thread through Codex app-server.
- The fork must report inherited HIGH before the Worker turn starts.
- `turn/start effort=low` performs the effort transition.
- Planner and Worker use the same structured-output schema.
- Codex multi-agent functionality is disabled by the transport.

Purpose: maximize inherited context and prompt-cache reuse.

### H — Fresh / High

- Planner/Supervisor: the same frozen PlannerResult; no Planner model call is repeated in the measured arm.
- Worker context: `WorkerContextStrategy.Fresh`.
- Worker reasoning: `WorkerReasoningEffort.High`.
- Every Worker receives only its bounded microtask and starts as a fresh ephemeral Codex session.
- Codex multi-agent functionality remains disabled.

Purpose: minimize agent lineage/fan-out while giving each small independent Worker stronger local reasoning.

## Frozen-plan requirement

Do not let each arm generate its own plan.

1. Prepare the benchmark workspace at the recorded base commit.
2. Run one plan-only mission with `StopAfterPlanning=true`.
3. Persist the exact `PlannerResult` JSON from that mission as the benchmark artifact.
4. Restore the workspace to the exact same base commit before each measured arm.
5. Create both measured missions with that same frozen PlannerResult.
6. Do not run the Planner again inside either measured arm.

The frozen plan is part of the benchmark input and must be preserved byte-for-byte once captured.

## Shared invariants

Keep identical between arms:

- LoopGolem commit;
- Codex CLI version;
- target repository and base commit;
- frozen PlannerResult JSON;
- mission goal;
- task ordering and dependencies;
- deterministic operations and checks;
- recovery limits;
- validation limits;
- Validator model/reasoning policy;
- host and WSL environment;
- external acceptance tests;
- no human intervention during a measured arm.

The Worker must not create sub-agents in either arm.

## Allowance measurement

The included-plan five-hour meter is coarse and may be quantized or delayed. Therefore:

1. stop other Work/Codex activity during the benchmark window;
2. record an explicit `account/rateLimits/read` snapshot before each arm;
3. record another immediately after the mission reaches a terminal state;
4. record follow-up snapshots after a short settling interval when practical;
5. also record the visible ChatGPT/Codex usage UI percentage;
6. never assign an account-level percentage delta to one individual turn.

If the meter changes before an arm begins while no inference is running, delay interpretation until it stabilizes.

## Required telemetry

For each arm record:

```text
Arm:
LoopGolem commit:
Frozen PlannerResult SHA-256:
Target base commit:
Codex CLI version:
Mission status:
Elapsed:
5h allowance before:
5h allowance immediately after:
5h allowance settled:
Visible UI remaining before:
Visible UI remaining after:

Input tokens:
Cached input tokens:
Cache-write input tokens:
Output tokens:
Reasoning output tokens:
Total tokens:

Supervisor sessions / turns:
Worker sessions / turns:
Worker resumed turns:
Validator sessions / turns:
Recovery cycles:
Validation cycles:

External acceptance tests:
Human intervention:
Notes:
```

## Success criteria

A strategy result is usable only when:

1. the mission reaches `Completed`;
2. the independent Validator returns `ok`;
3. deterministic build/self-tests pass;
4. external acceptance tests pass;
5. no manual edits or task intervention occur.

A cheaper failed arm does not beat a more expensive successful arm.

## Interpretation

The primary product metric is:

```text
useful completed work / five-hour allowance percentage point
```

Do not infer that cached-input pricing for purchased/API-style credits applies to the included five-hour allowance.

Possible outcomes:

- Fork/Low uses less allowance at equal quality: favor inherited-context mode for allowance-sensitive use.
- Fresh/High uses less allowance at equal quality: favor fresh strong micro-workers for allowance-sensitive use.
- Similar allowance, Fork/Low fewer effective tokens: keep both and distinguish token economics from allowance economics.
- Similar allowance, Fresh/High better quality or latency: prefer Fresh/High for the default allowance-oriented preset.
- Results vary materially between repetitions: run a reversed pair before selecting a default.

Regardless of the winner, LoopGolem should retain both policies because token-equivalent economics and included-plan allowance economics may optimize for different execution shapes.

## Current implementation support

The policy model exposes:

- `WorkerContextStrategy.Fresh`
- `WorkerContextStrategy.Affinity`
- `WorkerContextStrategy.SupervisorFork`
- `WorkerReasoningEffort.Low`
- `WorkerReasoningEffort.High`

Persisted missions created before these fields existed continue to derive their context behavior from the legacy `SessionReuseMode`.

The SupervisorFork path is experimental until it passes build/self-test validation and this controlled benchmark.
