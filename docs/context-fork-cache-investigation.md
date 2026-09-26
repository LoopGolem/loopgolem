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

## Current conclusions before Probe 3C

Established:

1. app-server can express HIGH parent -> HIGH fork -> LOW turn.
2. the original HIGH parent can conceptually remain a separate lineage for Supervisor/recovery use.
3. per-turn effort mutation is a real primitive in the tested Codex 0.156.1 protocol.
4. one app-server HIGH fork control inherited conversation context but reported zero last-turn cached input.
5. cache observations vary materially across runs, so paired controls remain necessary.
6. the first two app-server probe timeouts were probe-client bugs, not evidence of slow model inference.

Not established:

1. whether dynamic HIGH -> LOW `turn/start` preserves the inherited cached prefix;
2. whether LOW / Planner differs from HIGH / Planner after a proper dynamic effort change;
3. the incremental cache effect of PlannerSchema -> WorkerSchema;
4. whether a common role-agnostic output schema would materially improve cache reuse;
5. whether app-server migration is an efficiency win in end-to-end LoopGolem missions;
6. whether ChatGPT subscription-credit accounting follows any API-equivalent weighted-token heuristic.

## Probe 3C — next experiment

Do not change the experimental topology. Fix only the client transport.

Use one app-server process and four turns:

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

Critical client change:

Do not use `select()` on a buffered text wrapper.

Mirror the official SDK architecture instead:

```text
app-server stdout
        |
dedicated reader thread
        |
blocking readline()
        |
JSON decode
        |
Queue / router
        |
request and turn consumers
```

One reader must own stdout for the lifetime of the process. Consumers must never compete to read the pipe directly.

For every turn, persist and print immediately:

- input
- cached input
- cache-write input
- output
- reasoning output
- current thread reasoning effort
- turn status
- model-reported duration
- event ordering

Keep raw measurements primary.

Interpretation:

- compare HIGH / Planner to LOW / Planner for the effort-transition effect;
- compare LOW / Planner to LOW / Worker for the schema effect;
- if the HIGH control itself has unstable cache relative to nearby probes, avoid causal claims from a single sample.

## Production architecture implication

A migration from one-shot `codex exec` orchestration to Codex app-server is now a serious roadmap candidate because the desired fork/lineage and per-turn effort controls are exposed there.

This checkpoint does not yet justify implementing that migration before Probe 3C.

If/when LoopGolem adopts app-server, do not implement transport with ad-hoc competing reads from stdin/stdout. The production client should follow a single-reader/message-router design similar to the official SDK:

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

## Update rule

After Probe 3C, update this document in place with:

- the complete four-row result table;
- whether the HIGH -> LOW transition preserved cached input;
- whether PlannerSchema -> WorkerSchema changed cached input;
- whether app-server migration moves into the near-term roadmap;
- any revised experimental controls required before production implementation.
