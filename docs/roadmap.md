# Roadmap

This roadmap records the current engineering priorities for LoopGolem. It is intentionally evidence-driven: experimental findings should change priorities before large implementation work begins.

Last updated after C5 and the allowance-strategy implementation work on 2026-09-26.

## Current baseline

The current production architecture uses:

- a persistent GPT-6 Luna High Supervisor for planning and deterministic-failure recovery;
- bounded GPT-6 Luna Workers with explicit Low/High reasoning policy;
- deterministic host operations and verification;
- a separate persistent GPT-6 Luna High Validator;
- SQLite persistence for missions, tasks, attempts, agent sessions, turns and telemetry;
- explicit CLI transport modes: FreshEphemeral, NewPersistent and Resume;
- explicit Worker context strategies: Fresh, Affinity and experimental SupervisorFork;
- a narrow experimental app-server fork transport for Supervisor-derived Workers.

The current scheduler is serial even though mission tasks already contain dependency information.

## Release v0.1.0

The first release should remain focused on a coherent, reproducible and honestly documented baseline.

Release criteria:

1. current solution builds and deterministic self-tests pass;
2. install/run documentation is reproducible on supported Windows/WSL and Linux paths;
3. current architecture and mission protocol documentation match the implementation;
4. TaskForge benchmark results and their limitations remain documented;
5. the context/fork/cache investigation remains versioned;
6. known experimental limitations are explicit rather than hidden behind optimization claims;
7. the repository is tagged and released as v0.1.0.

Do not block v0.1.0 on a full app-server migration, parallel scheduling, or productionizing fork lineage. The shared Planner/Worker schema and narrow experimental SupervisorFork path now exist specifically for controlled measurement; broader transport changes still deserve isolated implementation and benchmarking.

## P0 — C5: isolate schema vs role text — completed

C5 closed the ambiguity left by C4.

Controlled result:

- Control A, PlannerSchema + neutral text: 25,088 / 25,324 cached input, 99.07%;
- Schema-only, WorkerSchema + the exact same neutral text: 0 / 25,152 cached input;
- Text-only, PlannerSchema + Worker-style text: 25,088 / 25,349 cached input, 98.97%;
- Control B, PlannerSchema + original neutral text: 25,216 / 25,324 cached input, 99.57%.

All comparison children forked from the same immutable HIGH parent checkpoint and changed to GPT-6 Luna Low only through `turn/start effort=low`.

Interpretation:

- changing PlannerSchema -> WorkerSchema alone destroyed the warmed prefix observed by the control;
- changing the role-specific user text alone preserved the same 25,088-token cached prefix;
- the final control remained strongly cache-positive, so cache availability did not disappear during the comparison window;
- the 128-token increase in Control B cached input is not a control loss and does not invalidate the preregistered C5 interpretation rule.

Therefore the current structured-output schema divergence is the primary cache-breaking component identified by C4/C5. Role-specific user text may still affect cache behavior in other shapes, but it was not the breaker in this controlled comparison.

The C5 script contained an extra exact-equality guard for Control A vs Control B and mechanically printed `inconclusive` because Control B cached 128 additional tokens. That guard was stricter than the roadmap's pre-run criterion, which invalidated the experiment only if Control B lost the cache hit. The documented interpretation follows the preregistered experiment design.

See `docs/context-fork-cache-investigation.md` for the complete result and limitations.

## P0.5 — Allowance-strategy benchmark

C4/C5 proved that Supervisor fork lineage can preserve a warm prompt cache when Planner and Worker use a stable structured-output schema. Separate observations of the included five-hour allowance, however, suggest that prompt-cache telemetry may not predict subscription-window consumption.

Before optimizing further for cached-token economics, compare two complete execution policies against the exact same frozen PlannerResult:

- **SupervisorFork + Low**: inherit the persistent HIGH Supervisor context, then lower the child on `turn/start`;
- **Fresh + High**: give each bounded microtask to a fresh HIGH Worker with no Supervisor lineage.

This is a product-strategy benchmark rather than a single-variable causal experiment. Its primary metric is useful completed work per visible five-hour allowance point. Raw input/cached/cache-write/output/reasoning telemetry remains mandatory but secondary.

Implementation support now includes explicit Worker context/reasoning policy, shared Planner/Worker structured output, an experimental app-server SupervisorFork transport, plan-only missions, missions created from a frozen PlannerResult, and `SupervisorSourceMissionId` binding from Arm F to the exact paused plan-only mission that generated that frozen result.

Release build with warnings-as-errors and Worker self-test are green on both Ubuntu and Windows for this pre-benchmark implementation. Do not select the product default until the controlled benchmark in `docs/benchmark-v3-allowance-strategy.md` has been run.

Regardless of the winner, preserve both execution policies because token-equivalent economics and included-plan allowance economics may favor different shapes.

## P1 — Stable agent-turn envelope

C5 identified the former PlannerSchema/WorkerSchema divergence as the primary cache-breaking component. A shared Planner/Worker structured-output envelope is now implemented for the experimental strategy work.

Further envelope optimization is paused until the allowance-strategy benchmark determines whether preserving inherited prompt cache is actually beneficial for the included five-hour Codex allowance.

Preferred direction:

- maximize a role-agnostic stable prefix;
- keep model, provider and common safety/capability instructions stable;
- place role-specific instructions as late as practical;
- use one common structured envelope if C5 demonstrates that schema divergence is the relevant cache breaker;
- keep Planner, Worker, Recovery and Validator semantics distinct even if they share a transport envelope.

A possible common envelope may contain optional fields for status/outcome, summary, tasks, checks, blocker, contextReuse and diagnosis.

Do not adopt a larger schema merely for aesthetic uniformity. Measure rendered-token overhead and cache behavior before production use.

## P2 — App-server transport adapter

C3/C4 validated the app-server lifecycle needed by LoopGolem:

~~~text
Supervisor HIGH
      |
      +-- fork child while still HIGH
      |       |
      |       +-- turn/start effort=LOW
      |
      +-- original Supervisor remains independent
~~~

C4 additionally showed that the HIGH -> LOW transition can preserve a warm prompt cache when the request shape remains compatible.

A narrow one-turn app-server SupervisorFork transport now exists behind the provider/transport boundary. The remaining P2 work is to evolve this into a production-grade managed adapter without rewriting mission semantics.

Required transport properties:

1. one long-lived app-server process per managed transport instance;
2. one dedicated blocking stdout reader;
3. a central JSON-RPC/message router;
4. response routing by request id;
5. notification routing by thread id and turn id;
6. explicit handling of server-to-client requests;
7. persisted provider thread ids and parent/child lineage;
8. per-turn reasoning-effort changes;
9. structured token-usage capture;
10. cancellation, restart and process-loss recovery;
11. codex exec fallback while app-server remains experimental.

The current experimental fork transport intentionally does not claim all of these production properties yet. It has passed deterministic build/self-test coverage for fork source selection and effort invariants, but still awaits the controlled allowance-strategy benchmark.

Never implement the production adapter using select() over a buffered TextIOWrapper; Probe 3B demonstrated why that reader design can hide already-buffered completion events.

## P3 — Explicit fork lineage

Once the app-server adapter is stable, add provider-independent lineage metadata for agent sessions.

Target shape:

~~~text
                     fork -> Worker Low A
                    /
Supervisor High ----
                    \
                     fork -> Worker Low B
~~~

Rules:

- the original Supervisor remains read-only and available for recovery;
- an independent task should fork from a clean planning checkpoint;
- a dependent task may fork/resume from the relevant predecessor branch when that lineage is intentionally useful;
- unclear lineage should fall back to a fresh, explicit handoff;
- filesystem state remains authoritative even when conversational lineage is inherited.

Persist enough lineage to explain which provider thread a Worker inherited from and why.

## P4 — Recovery diagnosis before repair

The TaskForge benchmark showed that a deterministic failure is not necessarily a source-code defect.

Recovery should distinguish at least:

- source defect;
- stale prerequisite/build artifact;
- invalid check invocation;
- environment/capability mismatch;
- unknown/ambiguous failure.

The Supervisor should be able to return a typed diagnosis/disposition before LoopGolem requires source-repair tasks.

A failed deterministic check must not automatically imply generate one or more Low repair tasks.

## P5 — Planning granularity and milestone gates

The current Planner emits a complete mission plan up front. Large fixed plans can become stale and can over-fragment work.

Future experiments should compare:

- one full up-front plan;
- milestone-based planning with deterministic gates;
- small batches of related Low work;
- reuse of a Low branch for several dependency-related tasks.

Do not assume that smaller microtasks are cheaper. Existing benchmarks do not establish that.

## P6 — Controlled end-to-end benchmark

After the transport/envelope changes stabilize, rerun TaskForge with stronger controls.

Preferred design:

- freeze or persist one PlannerResult where possible;
- compare transport/session policies against the same plan;
- preserve identical prompt bytes and target base commit;
- record raw input, cached input, cache-write input, output and reasoning separately;
- record session/turn lineage and recovery;
- require final Validator success plus external smoke tests;
- use paired/reversed repetitions when quota permits.

The existing A1/B1 pair remains historical evidence, not a clean causal measurement of Affinity.

## P7 — DAG-aware bounded parallelism

Parallel execution is intentionally later.

The mission graph already contains dependencies, but shared-workspace parallelism can corrupt attribution:

- concurrent Git diffs can contaminate task baselines;
- write-allowlist verification can see sibling edits;
- build/read operations can race;
- current session leases and deterministic assumptions are serial.

Before parallel scheduling, add either isolated Git worktrees per concurrent branch or rigorously disjoint write partitions plus concurrency-aware change attribution.

Parallelism should be bounded and cache-aware rather than maximized blindly.

## P8 — Self-hosting experiment

Only after recovery, app-server lifecycle and validation are robust enough, run a controlled mission in which LoopGolem modifies LoopGolem itself.

The running Worker must still obey the existing self-hosting rule: it does not hot-reload newly generated runtime code during the same mission.

Treat self-hosting as an integration benchmark, not as a prerequisite for v0.1.0.

## Measurement rules

Across all optimization work:

- raw token dimensions are primary;
- cachedInputTokens is a subset of input;
- account-level five-hour usedPercent is an operational constraint, not per-turn billing telemetry;
- cache-positive controls are required before drawing cache-preservation conclusions;
- change one experimental variable at a time;
- a result that reaches NeedsHumanAttention is not equivalent to mission success;
- do not claim cost superiority without a controlled measurement that supports it.

See also:

- docs/context-fork-cache-investigation.md
- docs/benchmark-v2.md
- docs/benchmarks/taskforge-v2-results.md
- docs/architecture.md
- docs/codex-integration.md
