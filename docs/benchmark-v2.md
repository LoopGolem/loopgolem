# Benchmark v2 — TaskForge

This runbook defines the controlled benchmark used to evaluate the post-v1 LoopGolem architecture.

The primary experiment is **Worker session reuse**, not task granularity. The current codebase keeps Planner/Supervisor, recovery, deterministic verification and the independent Validator identical between the two arms. Only `MissionPolicy.SessionReuse` changes.

## Question

Does bounded Luna Low context reuse reduce effective model cost and/or improve completion quality without weakening task isolation or deterministic verification?

Do not infer monetary cost from raw token totals alone. Record input, cached input, cache-write input, output and reasoning output separately. Provider pricing/discounts may treat those dimensions differently.

## Frozen architecture

Do not change these between paired runs:

- Planner/Supervisor: `gpt-6-luna / high`
- Worker: `gpt-6-luna / low`
- Validator: `gpt-6-luna / high`
- Worker session microtask cap: 3
- Worker session idle cap: 30 minutes
- Active Worker session cap: 4
- Maximum deterministic recovery cycles: 3
- Maximum validation cycles: 3
- Same LoopGolem commit
- Same Codex CLI version
- Same target repository base commit
- Same host/agent capability environment
- Same TaskForge prompt, byte-for-byte
- Same external acceptance tests
- No human intervention during a run

The historical TaskForge prompt begins with:

> Create a complete .NET 10 command-line application named TaskForge in this empty directory.

and ends with:

> The final result should be a finished, runnable project, not merely a scaffold.

The exact full prompt must be copied from the original benchmark record and saved verbatim before running this benchmark. Do not reconstruct it from memory. It also contained the instruction `Do not commit or push anything to Git.`

## Experimental arms

### A — Fresh Worker control

In the Desktop:

- enable **Use Codex**
- disable **Reuse Low context**

The created mission must show:

`Worker context: Fresh per task`

This persists `SessionReuseMode.Disabled` and forces Luna Low work through `FreshEphemeral`.

### B — Affinity reuse

In the Desktop:

- enable **Use Codex**
- enable **Reuse Low context**

The created mission must show:

`Worker context: Affinity`

This persists `SessionReuseMode.Affinity`. Luna Low may reuse a persistent Worker thread only when both its reuse hint and LoopGolem's deterministic affinity rules allow it.

Supervisor and Validator sessions remain persistent and independent in **both** arms.

## Workspace preparation

Use disposable benchmark clones/directories. Never run destructive reset commands in a workspace containing valuable uncommitted work.

Before the first run, record:

```text
LoopGolem commit:
Codex CLI version:
Target repository/base commit:
Host OS:
Agent runtime / WSL distribution:
TaskForge prompt SHA-256 (recommended):
```

Before every paired run, restore the target workspace to exactly the same base state. For a disposable Git clone this may be done with:

```bash
git reset --hard <TARGET_BASE_SHA>
git clean -fdx
git status --porcelain
```

The final command must produce no output.

Restarting the LoopGolem Worker between arms is recommended so both runs begin from a fresh Worker process. Mission persistence is scoped by mission id, so no AgentSession is intentionally shared across missions.

## Minimum procedure

1. Build LoopGolem Release and run the Worker self-test.
2. Prepare the disposable TaskForge workspace at the recorded base commit.
3. Start the Worker and Desktop.
4. Run arm A with the exact prompt.
5. Do not intervene while the mission executes.
6. Record the final mission status, telemetry card and external acceptance-test result.
7. Restore the TaskForge workspace to the exact same base commit.
8. Restart the Worker.
9. Run arm B with the exact same prompt.
10. Record the same evidence.
11. Compare the paired results before changing any policy.

If quota permits, run at least two paired repetitions and reverse the order on the second pair (A→B, then B→A) to reduce warm-cache/time-order bias.

## Required result record

For each run record:

```text
Arm:
LoopGolem commit:
Target base commit:
Codex CLI version:
Mission status:
Elapsed:
Input tokens:
Cached input tokens:
Cache-write input tokens:
Output tokens:
Reasoning output tokens:
Comparable total tokens:
Sessions:
Turns:
Worker resumed turns:
Worker reuse hints:
Recovery cycles:
Successful recovery cycles:
Exhausted recovery cycles:
Supervisor turns / total tokens:
Worker turns / total tokens:
Validator turns / total tokens:
External acceptance tests:
Human intervention:
Notes:
```

The Desktop mission telemetry card exposes these model/session/recovery dimensions. Task rows retain per-task total tokens and state.

## Quality criteria

A run is considered functionally successful only when all of the following are true:

1. LoopGolem reaches `Completed`.
2. The final independent Validator returns `ok`.
3. Required deterministic build/self-tests complete successfully.
4. The external TaskForge acceptance/smoke tests pass.
5. No manual code edits or task intervention occurred during the run.
6. The final output satisfies the original prompt rather than merely compiling.

A `NeedsHumanAttention` result is not equivalent to success, even if the workspace is partially usable.

## Efficiency interpretation

Compare the two current-code arms first. This is the causal A/B for Worker context reuse.

Particular signals of interest:

- lower non-cached input while quality stays constant;
- higher cached-input share;
- lower or stable output/reasoning tokens;
- fewer Worker sessions for a similar number of microtasks;
- non-zero Worker resumed turns in the Affinity arm;
- fewer recovery cycles or validation corrections;
- lower elapsed time;
- successful completion where the control fails.

Do not optimize task granularity, model reasoning effort, recovery limits or validation policy in the same experiment.

## Historical reference

The pre-redesign measurements remain useful as historical context, but they are **not** a causal control for the current architecture because steps 1–7 changed persistence, recovery, capabilities and session lifecycles.

Historical monolithic Codex reference:

- GPT-6 Luna High
- elapsed: 408.05 s
- input: 380,419
- cached input: 350,208
- output: 15,420
- reasoning output: 5,733
- comparable input + output: 395,839
- completed the task and external smoke tests
- one solution configuration defect was observed

Historical LoopGolem v1 reference:

- Planner: GPT-6 Luna High
- Workers: GPT-6 Luna Low
- elapsed before failure: 636.216 s
- eight completed Worker calls had 683,608 known raw tokens in aggregate
- mission failed at deterministic build before final Validator
- the host build surfaced 67 compile errors
- no human intervention

The v1 raw Worker total must not be converted directly into a monetary comparison because the old capture did not preserve all pricing-relevant token dimensions with the fidelity of the current telemetry.

## After the benchmark

Change one variable at a time.

If Affinity is cheaper/better, the next experiments may tune the microtask cap or affinity threshold. If it is not, keep the current telemetry and investigate where resumed sessions are paying for stale context before changing task granularity.
