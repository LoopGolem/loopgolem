# Context, fork and prompt-cache investigation

This document is a versioned checkpoint of the investigation into Codex session lineage, reasoning-effort changes, structured-output schemas and prompt-cache behavior.

It intentionally records incomplete and negative results as well as successful ones. Do not treat a single cache observation as a general provider guarantee.

Current implementation reference at the start of this investigation: `42eb47050c87480c7883682563aac48457b9195f`.

Codex runtime used by the probes: `codex-cli 0.156.1`, exact upstream tag `rust-v0.156.1`.

## Why this investigation exists

LoopGolem currently uses `codex exec` transport modes:

- `FreshEphemeral`
- `NewPersistent`
- `Resume`

The current `Resume` path requires the same logical role, model and reasoning effort. That means the production transport cannot currently express the desired lineage:

```text
Planner / Supervisor HIGH
        |
        +-- fork -> Worker LOW
        |
        +-- original Supervisor remains HIGH/read-only
```

The architectural hypothesis is that a Worker fork can inherit the expensive repository/global understanding created by the HIGH Supervisor while the original Supervisor remains clean for later recovery.

A related hypothesis is that a stable structured-output envelope could improve prompt-prefix reuse across Planner and Worker roles. Both hypotheses require measurement before production changes.

## Exact Codex 0.156.1 primitives verified

Inspection of the exact upstream `rust-v0.156.1` source established:

- `codex exec fork <thread-id>` exists and creates a new thread from a prior session.
- app-server `thread/fork` forks an existing thread.
- app-server `turn/start` accepts an optional per-turn `effort`.
- `TurnStartParams.effort` is documented by the generated protocol as an override for the current and subsequent turns.
- app-server thread responses expose the current `reasoningEffort`.
- `thread/tokenUsage/updated` exposes last-turn and cumulative token dimensions.
- feature `reasoning_effort_override` exists in 0.156.1 and is under development.
- the upstream reasoning-effort tests exercise effort changes by preserving a thread/session and applying a configuration update, rather than merely launching a new process with a different startup effort.

Relevant upstream implementation areas include:

- `codex-rs/exec/src/cli.rs`
- `codex-rs/exec/src/lib.rs`
- `codex-rs/core/src/session/reasoning_effort.rs`
- `codex-rs/features/src/lib.rs`
- `codex-rs/app-server-protocol/schema/typescript/v2/TurnStartParams.ts`
- `codex-rs/app-server-protocol/schema/typescript/v2/ThreadForkParams.ts`
- `codex-rs/app-server-protocol/schema/typescript/v2/ThreadTokenUsageUpdatedNotification.ts`
- `sdk/python/src/openai_codex/_run.py`
- `sdk/python/src/openai_codex/_message_router.py`

This source evidence motivated moving the experiment from one-shot `codex exec` calls to one persistent app-server process.

## Cache accounting discipline

Raw token dimensions are primary:

- input
- cached input
- cache-write input
- output
- reasoning output

For convenience only, some probe notes also calculate:

```text
ordinary input = input - cached - cache-write
weighted input = ordinary + 0.10 * cached + 1.25 * cache-write
```

The weighted value is an API-equivalent heuristic. It is not evidence about ChatGPT subscription-credit consumption and must not replace the raw measurements.

## Probe 1 — codex exec fork, startup effort changes

Shape:

```text
Parent: Luna HIGH + PlannerSchema
  +-- fork HIGH + PlannerSchema
  +-- fork LOW  + PlannerSchema
  +-- fork LOW  + WorkerSchema
```

Results:

| Run | Input | Cached | Cache write | Ordinary | Cache hit | Weighted input | Output | Reasoning |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| parent HIGH / Planner | 33,033 | 12,032 | 0 | 21,001 | 36.42% | 22,204.2 | 521 | 79 |
| fork HIGH / Planner | 53,565 | 31,232 | 0 | 22,333 | 58.31% | 25,456.2 | 639 | 108 |
| fork LOW / Planner | 53,565 | 12,032 | 0 | 41,533 | 22.46% | 42,736.2 | 597 | 79 |
| fork LOW / Worker | 53,419 | 12,032 | 0 | 41,387 | 22.52% | 42,590.2 | 613 | 79 |

Observed:

- programmatic fork worked;
- the same-effort HIGH control had 31,232 cached input;
- both LOW rows fell to 12,032 cached input;
- the difference between HIGH control and LOW same-schema was 19,200 cached tokens.

Limitation:

The LOW children were launched as separate `codex exec` processes with LOW configured at process startup. This does not isolate the app-server/configuration-update mechanism used by the exact 0.156.1 reasoning-effort tests.

Therefore this result does not prove that a dynamic HIGH -> LOW turn transition destroys cache.

## Probe 2 — codex exec fork with reasoning_effort_override enabled

The same basic `codex exec` shape was rerun with `reasoning_effort_override` enabled, but the child process still started with the target effort.

Results:

| Run | Input | Cached | Cache write | Ordinary | Cache hit | Weighted input | Output | Reasoning |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| parent HIGH / Planner + override | 33,171 | 12,032 | 0 | 21,139 | 36.27% | 22,342.2 | 673 | 87 |
| fork HIGH / Planner + override | 53,855 | 12,032 | 0 | 41,823 | 22.34% | 43,026.2 | 794 | 125 |
| fork LOW / Planner + override | 53,855 | 12,032 | 0 | 41,823 | 22.34% | 43,026.2 | 761 | 87 |
| fork LOW / Worker + override | 53,709 | 12,032 | 0 | 41,677 | 22.40% | 42,880.2 | 753 | 87 |

Observed:

- every row, including HIGH -> HIGH, hit the same 12,032 cached-input floor;
- the extra 19,200 cached segment seen by the Probe 1 HIGH control was unavailable even to the same-effort control.

Interpretation:

This run cannot show that `reasoning_effort_override` failed. Cache/routing behavior changed between runs, and the test still changed effort at process startup instead of using app-server `turn/start effort=...`.

Schema impact also remained unresolved.

## Probe 3A — first app-server lifecycle probe

Intended shape:

```text
Parent HIGH
    |
    +-- fork HIGH / Planner      control A
    +-- fork HIGH -> turn LOW / Planner
    +-- fork HIGH -> turn LOW / Worker
    +-- fork HIGH / Planner      control B
```

The probe used one persistent `codex app-server --listen stdio://` process with `reasoning_effort_override` enabled.

Important structural result:

- parent was created with `reasoningEffort='high'`;
- LOW candidate child was forked without an effort override and reported inherited `reasoningEffort='high'`;
- only afterward did `turn/start effort='low'` run;
- a subsequent `thread/read` reported the child at `reasoningEffort='low'`.

Therefore the following lifecycle is supported by the tested app-server/runtime combination:

```text
Parent HIGH
    -> fork child that is still HIGH
    -> turn/start effort=LOW
    -> child becomes LOW
```

This is the lineage primitive LoopGolem currently cannot express with its production `codex exec` transport abstraction.

### 3A failure

The first custom JSON-RPC collector waited for both:

- `turn/completed`; and
- a matching `thread/tokenUsage/updated`.

If completion arrived without a usage event captured by the collector, it kept waiting.

Inspection of the exact official Python SDK showed that this requirement was too strict. The SDK collects usage when available, but `turn/completed` is terminal for the turn stream.

The 3A timeout therefore did not establish a model timeout.

## Probe 3B — terminal completion fix, then transport-reader bug

3B changed the collector so `turn/completed` was terminal and saved every JSON-RPC message to `trace.jsonl`.

### Parent result

```text
parent HIGH / Planner
Input:        20,252
Cached:       12,032
Cache write:       0
Ordinary:      8,220
Cache hit:     59.41%
Weighted:     9,423.20
Output:             85
Reasoning:          49
```

The parent completed normally.

### HIGH fork control observation

The HIGH control was forked from the parent and inherited `reasoningEffort='high'`.

The last-turn token-usage event reported:

```text
Input:        25,214
Cached:            0
Cache write:       0
Output:            35
Reasoning:          0
```

The cumulative usage in the same event still contained the parent's 12,032 cached tokens, confirming that `last.cachedInputTokens=0` was a genuine last-turn measurement rather than confusion with the cumulative total.

The turn's final answer explicitly referenced the inherited synthetic context, so conversation lineage existed even though this one request reported zero cached input.

Do not generalize this one observation into “app-server fork does not cache”. Earlier controls showed material run-to-run cache variation.

### Why 3B appeared to hang

The trace showed that the model did not actually take five minutes.

The HIGH control's final `turn/completed` reported:

```text
durationMs: 8402
```

Its `emittedAtMs` matched the preceding usage/rate-limit notifications, but the custom Python client did not read the completion until almost five minutes later.

Root cause:

The probe opened app-server stdout as buffered text and then combined:

```python
select.select([proc.stdout], ...)
proc.stdout.readline()
```

`TextIOWrapper.readline()` can prefetch multiple lines into Python's user-space buffer. `select()` observes only the OS file descriptor, not unread lines already buffered inside `TextIOWrapper`.

Consequently:

1. a read consumed one JSON line while later lines were already prefetched;
2. the OS pipe became empty;
3. `select()` blocked even though Python already held unread `thread/status/changed` and `turn/completed` lines;
4. a later write to app-server woke the descriptor;
5. the client immediately surfaced the old, already-emitted notifications.

The trace therefore absolved both the model and app-server from the apparent five-minute stall. The custom probe transport was incorrect.

The cleanup `thread/archive` also appeared to time out for the same reader reason; the trace subsequently showed the parent becoming `notLoaded`.

## Probe 3C — corrected app-server reader, complete run

Probe 3C kept the same experimental topology as 3B but replaced the custom `select()` + buffered-text reader with a single dedicated stdout reader thread using blocking `readline()`, then routed JSON-RPC responses and turn notifications through queues.

The complete run succeeded.

Shape:

```text
Parent HIGH / Planner
        |
        +-- fork HIGH / Planner
        |       control
        |
        +-- fork HIGH -> turn LOW / Planner
        |       isolate effort transition
        |
        +-- fork HIGH -> turn LOW / Worker
                isolate schema change
```

Results:

| Run | Input | Cached | Cache write | Ordinary | Cache hit | Weighted input | Output | Reasoning | Duration ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| parent HIGH / Planner | 20,252 | 0 | 0 | 20,252 | 0.00% | 20,252.00 | 127 | 76 | 4,576 |
| fork HIGH / Planner | 25,256 | 0 | 0 | 25,256 | 0.00% | 25,256.00 | 36 | 0 | 3,948 |
| fork LOW / Planner | 25,256 | 0 | 0 | 25,256 | 0.00% | 25,256.00 | 36 | 0 | 4,239 |
| fork LOW / Worker | 25,098 | 0 | 0 | 25,098 | 0.00% | 25,098.00 | 48 | 0 | 5,555 |

Structural observations:

- the parent started at `reasoningEffort='high'`;
- each child fork was created without an effort override and reported inherited `reasoningEffort='high'`;
- the LOW Planner and LOW Worker children changed to LOW only through `turn/start effort='low'`;
- after those turns, `thread/read` reported `reasoningEffort='low'`;
- every turn completed normally;
- the single-reader/message-router transport eliminated the false five-minute hangs seen in 3A/3B.

### What 3C proves

Probe 3C validates the app-server lifecycle and client architecture required to represent:

```text
HIGH parent
    -> fork child while still HIGH
    -> lower the child to LOW on turn/start
    -> keep the original parent lineage independent
```

The corrected single-reader transport also validates the implementation pattern needed for any production app-server adapter: one stdout owner, request-id routing, turn-id routing and terminal `turn/completed` handling.

### What 3C does not prove

All four requests reported `cachedInputTokens=0`.

Therefore Probe 3C cannot measure:

- whether dynamic HIGH -> LOW effort changes preserve or destroy an existing cached prefix;
- whether PlannerSchema -> WorkerSchema changes reduce an existing cache hit;
- whether a common role-agnostic output schema would improve cache reuse.

The exact equality of input tokens for HIGH / Planner and LOW / Planner (25,256 each) confirms that the two arms were closely matched at the visible-input level, but zero cache in both arms prevents any cache-preservation conclusion.

The WorkerSchema arm used 25,098 input tokens, 158 fewer than the PlannerSchema arms. That difference describes rendered/request input size for this probe; it is not evidence of a cache advantage because neither arm had a cache hit.

### Important cache-variability observation

Cache availability varied materially across otherwise related probes:

- Probe 1 parent: 12,032 cached; HIGH fork: 31,232 cached.
- Probe 2 parent and all children: 12,032 cached.
- Probe 3B parent: 12,032 cached; HIGH fork: 0 cached.
- Probe 3C parent and all children: 0 cached.

This is strong evidence that a single uncached app-server run cannot answer the effort/schema cache question.

Current OpenAI prompt-caching documentation explicitly notes that maintaining a session does not guarantee a cache hit. Cache reuse requires an eligible matching rendered prefix to be available on the machine handling the request.

## Current conclusions after Probe 3C

Established:

1. app-server can express HIGH parent -> HIGH fork -> LOW turn.
2. the original HIGH parent can remain an independent lineage for Supervisor/recovery use.
3. per-turn effort mutation works in the tested Codex 0.156.1 protocol/runtime.
4. conversation lineage and prompt-cache reuse are distinct: a child can inherit conversation context while a request reports zero cached input.
5. a production app-server client should use one dedicated stdout reader with message routing; the `select()` + buffered `TextIOWrapper` pattern used in 3B is invalid.
6. app-server is now a concrete architecture candidate because it exposes lineage and per-turn controls that the current LoopGolem `codex exec` abstraction cannot represent.
7. app-server has not yet been shown to reduce model cost or cached-input usage in an end-to-end LoopGolem mission.

Not established:

1. whether dynamic HIGH -> LOW `turn/start` preserves an already-warm cached prefix;
2. the incremental cache effect of PlannerSchema -> WorkerSchema when a cache hit actually exists;
3. whether a common role-agnostic output schema materially improves cache reuse;
4. whether app-server migration is an efficiency win rather than primarily a lifecycle/control improvement;
5. whether ChatGPT subscription-credit accounting follows any API-equivalent weighted-token heuristic.

## Next cache experiment

Do not repeat Probe 3C unchanged. A new experiment must first establish a warm-cache precondition and only then compare effort/schema arms.

Required experimental discipline:

1. create or repeat a deterministic request until a control request demonstrably reports non-zero cached input;
2. immediately fork all comparison arms from the same parent/checkpoint;
3. include a same-effort/same-schema cache-positive control;
4. compare HIGH / Planner vs LOW / Planner only if the control remains cache-positive;
5. compare LOW / Planner vs LOW / Worker only if the LOW / Planner arm also has a cache-positive reusable prefix;
6. abort the causal interpretation if the control reports zero cached input.

The objective of the next probe is therefore not “run the four arms again”; it is “obtain a cache-positive control, then isolate effort and schema changes before that cache state disappears.”

## GPT-6 Luna identity and 5-hour allowance observation

After Probe 3C, the app-server trace was inspected to verify the model actually associated with the parent thread rather than trusting only the requested slug.

The `thread/start` response reported:

```text
model:           gpt-6-luna
modelProvider:   openai
serviceTier:     null
reasoningEffort: high
```

This rules out the hypothesis that the probe silently fell back to GPT-6 Astra or GPT-6 Sol at thread creation.

During the same 3C run, `account/rateLimits/updated` notifications for the primary 300-minute window reported:

| Local time | Used percent |
| --- | ---: |
| 00:25:31 | 10% |
| 00:25:36 | 10% |
| 00:25:40 | 13% |
| 00:25:46 | 15% |

Do not attribute these deltas directly to individual turns. In Codex 0.156.1, `account/rateLimits/updated` is an account-level sparse rolling snapshot and carries no thread id or turn id. It is not a per-turn billing/debit event. Backend accounting may be delayed, coalesced or quantized relative to individual model completions.

The observation is nevertheless significant: the account-level five-hour meter increased by five percentage points during the short interval covering the 3C probe. This appears unexpectedly large relative to earlier LoopGolem missions and warrants a separate allowance-meter investigation before more quota-intensive cache probes.

Current official documentation says Work and Codex share the included plan allowance and that actual usage depends on model, task size, reasoning settings and amount of work. GPT-6 Luna is an official Work/Codex model and is also substantially cheaper per token than GPT-6 Sol/Astra in current token-based pricing. The included five-hour meter must therefore not be inferred directly from raw token counts or API-equivalent dollar pricing.

Next allowance investigation should use explicit `account/rateLimits/read` snapshots immediately before and after a deliberately tiny, isolated single turn, with no concurrent Work/Codex activity, then repeat by effort/model if needed. The purpose is to characterize meter behavior, not to assume each rolling notification is a turn-scoped charge.

## Probe Q1 — allowance-meter isolation protocol

Before any further cache-intensive probe, characterize the account-level five-hour allowance meter with a deliberately tiny isolated experiment.

Q1 protocol:

1. start one app-server process with no model turn yet;
2. call `account/rateLimits/read` three times across approximately 15 seconds;
3. if the primary 300-minute `usedPercent` changes during this no-inference baseline, abort before model use because prior accounting is still settling;
4. only on a stable baseline, create one fresh `gpt-6-luna / low` thread and verify the actual `ThreadStartResponse.model` and `reasoningEffort`;
5. run one minimal no-tool turn;
6. record the turn's raw token usage and duration;
7. read `account/rateLimits/read` immediately after completion, then approximately +10 seconds and +30 seconds;
8. archive/close the temporary thread and persist the complete JSON-RPC trace.

Q1 is not a pricing benchmark. Its purpose is to determine whether the five-hour meter updates synchronously enough to correlate a single isolated Luna Low turn with an account-level percentage delta. If the meter is unchanged, delayed or quantized, do not infer a per-turn cost from it.

## Probe Q1 result — isolated Luna Low allowance meter

Q1 completed successfully with no concurrent model work during the probe itself.

### Stable no-inference baseline

Three explicit `account/rateLimits/read` snapshots were taken before any model turn:

| Snapshot | Local time | Primary 5h used | Secondary used |
| --- | --- | ---: | ---: |
| baseline t=0s | 00:48:54 | 18% | 39% |
| baseline t=+5s | 00:49:00 | 18% | 39% |
| baseline t=+15s | 00:49:10 | 18% | 39% |

The baseline was stable, so Q1 proceeded with exactly one model turn.

### Actual model identity

The fresh thread response reported:

```text
model:           gpt-6-luna
modelProvider:   openai
serviceTier:     null
reasoningEffort: low
```

### Single tiny turn

The user instruction was only to reply `OK` and not call tools.

Observed turn telemetry:

```text
inputTokens:            12,626
cachedInputTokens:           0
cacheWriteInputTokens:       0
outputTokens:                5
reasoningOutputTokens:       0
durationMs:              2,231
status:              completed
```

The 12,626 input tokens show that even a tiny user message carries a substantial fixed Codex-rendered prompt/context overhead in this environment.

### Post-turn allowance reads

| Snapshot | Local time | Primary 5h used | Secondary used |
| --- | --- | ---: | ---: |
| after t=0s | 00:49:13 | 18% | 39% |
| after t=+10s | 00:49:24 | 18% | 39% |
| after t=+30s | 00:49:44 | 18% | 39% |

The single isolated Luna Low turn did not move either displayed integer percentage within 30 seconds.

### Interpretation

Q1 disproves the naive interpretation that each small Luna Low turn necessarily consumes one or more visible percentage points of the five-hour meter immediately. The 3C rolling notifications `10% -> 10% -> 13% -> 15%` therefore must not be mapped directly onto individual 3C turns.

Q1 does not establish the exact hidden fractional cost of the tiny turn because the displayed percentage is integer-valued and backend accounting may be delayed or quantized. A turn can consume non-zero allowance without crossing the next displayed integer boundary.

The first Q1 baseline was 18%, whereas the final rolling snapshot observed during 3C was 15%. Without a continuous isolated observation window between the experiments, the extra three percentage points cannot be causally assigned. If no other Work/Codex activity occurred in that interval, delayed/coalesced accounting from earlier probes becomes a plausible explanation, but it remains unproven.

Practical consequence: do not use per-turn `account/rateLimits/updated` deltas as a cost metric. Continue to record raw token telemetry as the primary per-turn measurement and treat the five-hour percentage only as a coarse account-level operational constraint.

## Probe C4 — cache-positive fork/effort/schema protocol

C4 is the first cache experiment that is conditional on proving the relevant child prefix is actually warm before spending LOW comparison turns.

Topology:

```text
Parent HIGH / Planner
        |
        +-- HIGH / Planner warm attempt 1
        +-- HIGH / Planner warm attempt 2 (if needed)
        +-- HIGH / Planner warm attempt 3 (if needed)
                |
                +-- proceed only after Cached > 0
                        |
                        +-- LOW / Planner
                        +-- LOW / Worker
                        +-- HIGH / Planner final control
```

All children fork from the same immutable parent checkpoint. The Planner children use the same follow-up text and PlannerSchema. The LOW / Worker arm changes only the output schema/follow-up required by WorkerSchema. LOW effort is applied dynamically after forking a HIGH child, using the app-server per-turn effort mechanism already validated by Probe 3C.

Causal interpretation rules:

1. If no HIGH / Planner warm attempt reports non-zero cached input, abort before any LOW turn. C4 is inconclusive and consumes no LOW comparison quota.
2. The first cache-positive HIGH / Planner child is the pre-comparison control.
3. Compare that cache-positive HIGH / Planner control with LOW / Planner to test whether the dynamic HIGH -> LOW transition preserves the warmed prefix.
4. Compare LOW / Planner with LOW / Worker to observe the incremental effect of PlannerSchema -> WorkerSchema, but only if LOW / Planner itself remains cache-positive.
5. Run a final HIGH / Planner control immediately afterward. If the final HIGH control loses the cache hit, treat cache routing/availability as unstable and avoid causal claims about either LOW arm.
6. Record input, cached input, cache-write input, output, reasoning output, duration, and post-turn reasoning effort for every arm.

Bounded quota rule: at most three HIGH warming attempts. Do not loop until a hit indefinitely.

## Production architecture implication

Migration from one-shot `codex exec` orchestration to Codex app-server should now appear on the LoopGolem roadmap as a serious post-v0.1 architecture item.

The reason is lifecycle capability, not yet proven cache savings:

- explicit thread fork lineage;
- per-turn reasoning-effort changes;
- long-lived session control;
- richer turn lifecycle;
- direct token-usage notifications;
- an architecture compatible with future parent/child worker branches.

Do not justify the migration with a claim that app-server is cheaper. The current probes do not support that claim.

If/when LoopGolem adopts app-server, the production adapter should follow a single-reader/message-router design similar to the official SDK:

- one long-lived app-server process;
- one dedicated stdout reader;
- JSON-RPC response routing by request id;
- notification routing by thread/turn id;
- bounded turn lifecycle state;
- persisted provider thread identity;
- explicit fork lineage;
- robust handling of server->client requests;
- token telemetry captured independently from terminal completion;
- restart/recovery rules that remain provider-independent above the adapter boundary.

## Related benchmark evidence

The controlled TaskForge A1/B1 benchmark remains documented separately in:

- `docs/benchmark-v2.md`
- `docs/benchmarks/taskforge-v2-results.md`

Those results showed that Fresh vs Affinity was not causally resolved: both missions reached `NeedsHumanAttention`, planning/failure modes diverged, and derived non-cached input was nearly equal even though B1 had much more cached raw input.

Do not mix the TaskForge Affinity result with this fork/cache probe as if they were one experiment.


## Probe C4 result — cache-positive control

C4 completed successfully.

| Run | Input | Cached | Cache hit | Ordinary | Output | Reasoning | Duration ms | Effort |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| parent HIGH / Planner | 20,371 | 0 | 0.00% | 20,371 | 41 | 0 | 4,009 | high |
| HIGH warm/control 1 | 25,289 | 0 | 0.00% | 25,289 | 35 | 0 | 4,123 | high |
| HIGH warm/control 2 | 25,289 | 25,088 | 99.21% | 201 | 35 | 0 | 3,574 | high |
| LOW / Planner | 25,289 | 25,088 | 99.21% | 201 | 35 | 0 | 4,574 | low |
| LOW / Worker | 25,131 | 0 | 0.00% | 25,131 | 48 | 0 | 4,934 | low |
| HIGH final control | 25,289 | 25,088 | 99.21% | 201 | 35 | 0 | 3,444 | high |

The dynamic HIGH -> LOW transition preserved the warmed cache when the Planner request shape was retained: both HIGH control A and LOW / Planner reported exactly 25,088 cached tokens out of 25,289 input.

The final HIGH control again reported 25,088 cached tokens, so cache availability remained stable across the comparison window.

LOW / Worker reported zero cached tokens. Since this arm changed both the structured-output contract and the role-specific follow-up text, C4 proves that the current Planner and Worker request shapes are not cache-compatible, but does not yet isolate whether schema, user text, or another rendered-request difference is the exact cause.

Architectural implication: app-server fork lineage plus a dynamic HIGH -> LOW effort change is now experimentally supported as a cache-preserving path in the same-request-shape case. A follow-up experiment should isolate structured-output schema and role-specific prompt text separately before redesigning the production envelope.

The account five-hour meter moved from 18% to 24% across the full C4 process. Per Q1, this coarse account-level percentage is not used as per-turn cost telemetry.


### C4 allowance UI corroboration

Immediately after C4, the ChatGPT usage UI showed **76% remaining** in the five-hour window, i.e. **24% used**, with no separate Codex use reported during the interval. This independently matches C4's explicit app-server snapshots of 18% before and 24% after.

This strengthens the conclusion that the account-level meter genuinely advanced by six visible percentage points during the C4 observation window. It still does not make the meter suitable for per-turn attribution: the six points cannot be assigned reliably among the parent, warm controls, LOW Planner, LOW Worker, final HIGH control, or delayed accounting from earlier activity.

## Probe C5 result — schema vs role text isolation

C5 isolated the two request-shape changes that were confounded in C4: structured-output schema and role-specific user text.

All comparison children forked from the same immutable HIGH parent checkpoint, inherited HIGH at fork time, and changed to GPT-6 Luna Low only through `turn/start effort=low`. The warm-up stage used HIGH only and the comparison did not proceed until a cache-positive control existed.

Results:

| Run | Input | Cached | Cache write | Cache hit | Output | Reasoning | Duration ms | Effort |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| parent HIGH / Planner | 20,371 | 12,032 | 0 | 59.06% | 82 | 45 | 5,732 | high |
| HIGH warm 1 | 25,324 | 0 | 0 | 0.00% | 47 | 20 | 5,974 | high |
| HIGH warm 2 | 25,324 | 25,088 | 0 | 99.07% | 54 | 24 | 4,205 | high |
| Control A / PlannerSchema + neutral text | 25,324 | 25,088 | 0 | 99.07% | 37 | 15 | 4,222 | low |
| Schema-only / WorkerSchema + same neutral text | 25,152 | 0 | 0 | 0.00% | 63 | 14 | 4,960 | low |
| Text-only / PlannerSchema + Worker-style text | 25,349 | 25,088 | 0 | 98.97% | 118 | 90 | 6,516 | low |
| Control B / PlannerSchema + original neutral text | 25,324 | 25,216 | 0 | 99.57% | 41 | 14 | 3,994 | low |

### Interpretation

C5 provides strong evidence that the structured-output schema change is the primary cache-breaking component observed in C4.

The decisive comparison is:

- Control A used PlannerSchema plus the neutral text and cached 25,088 input tokens.
- Schema-only changed only PlannerSchema -> WorkerSchema while keeping the same neutral text, and cached input fell to zero.
- Text-only retained PlannerSchema but changed the user text to a Worker-style instruction, and the same 25,088-token cached prefix remained available.

The final Control B remained strongly cache-positive and used the same total input as Control A. It reported 25,216 cached tokens, 128 more than Control A. This does not represent a control-cache loss. The preregistered C5 invalidation rule was to avoid causal interpretation if Control B lost the cache hit; that condition did not occur.

The probe script additionally contained a stricter, non-preregistered guard requiring exact equality between Control A and Control B token dimensions. That guard classified the run as `inconclusive_control_drift`. In retrospect, exact equality was unnecessarily strict: the final control retained and slightly expanded the cached prefix rather than losing it. The scientific interpretation follows the experiment design recorded before the run, not that extra implementation guard.

C5 therefore resolves the main C4 ambiguity:

1. dynamic HIGH -> LOW still preserves a warmed prefix when the structured-output schema remains stable;
2. changing the role-specific user text alone did not destroy that warmed prefix in this probe;
3. changing PlannerSchema -> WorkerSchema alone destroyed the observed warmed prefix completely;
4. the next design work should prioritize a stable/common structured-output envelope before optimizing role-text placement.

This does not prove that every arbitrary schema change always causes a full miss, nor that role text can never matter. It establishes the behavior of the current PlannerSchema/WorkerSchema pair under this controlled forked-context protocol.

### Five-hour allowance observation

The user observed an additional six visible percentage points consumed from the five-hour allowance around the C5 testing window. The setup failures preceding the successful run aborted before any model turn, but per Q1 the account-level percentage remains coarse, integer-valued and potentially delayed/coalesced. Do not assign those six points to individual C5 turns or derive per-turn cost from the UI meter.

Raw per-turn token telemetry remains the primary experimental measurement.

