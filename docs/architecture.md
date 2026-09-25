# Architecture

LoopGolem is a persistent orchestrator rather than a long-lived chat process.

## Components

- **LoopGolem.Core**: provider-independent mission, task, quota, protocol and policy concepts.
- **LoopGolem.Orchestrator**: state transitions, dependency scheduling, retry policy, validation cycles and completion rules.
- **LoopGolem.Worker**: background mission execution, SQLite persistence, Codex integration, local verification and IPC; restartable independently of the UI.
- **LoopGolem.Desktop**: Avalonia Windows/Linux client; product logic does not belong here.
- **LoopGolem.Cli**: automation and power-user surface over the same worker contracts.

## Planner-driven mission flow

The current Codex execution architecture follows the principle: expensive intelligence decides; cheap intelligence executes; deterministic software verifies.

1. A mission starts with deterministic workspace inspection and project discovery.
2. Before agent work, the Worker persists a capability snapshot that distinguishes the Codex agent environment from the deterministic host environment. The initial probe records `git` and `dotnet` availability/version in each environment and is reused after restart.
3. GPT-6 Luna High opens one persistent read-only Supervisor session for the mission. Its first turn is planning: it can inspect the repository, receives the capability snapshot, and returns a structured dependency graph of small tasks. Later deterministic-recovery turns resume this same Supervisor when available.
4. Exact mechanical work uses local deterministic operations such as `write_file`, `create_directory`, `rename_path` and direct `run_command` execution on the host.
5. Tasks requiring implementation judgment run as bounded GPT-6 Luna Low workers in the agent environment. Each microtask receives its bounded prompt, explicit read files, explicit write allowlist, acceptance checks and the environment capability snapshot. When policy and deterministic affinity agree, several sequential microtasks may reuse one persistent Worker thread without merging their task semantics.
6. LoopGolem verifies that a Luna Low worker did not change files outside its write allowlist.
7. After a task batch, LoopGolem runs deterministic Git inspection and the available local .NET build verification.
8. LoopGolem creates an **unreachable Git snapshot commit** from the working tree using a temporary index. The user's branch, index and HEAD are not moved.
9. GPT-6 Luna High opens a separate persistent read-only Validator session. It receives the original user goal, original plan, base commit, snapshot commit and the same persisted capability snapshot, and validates the actual diff. It never shares context with the Supervisor.
10. If validation returns `ok`, the Validator session closes and the mission completes. If it returns `not_ok`, the validator may return a small correction task batch in the same deterministic/Luna Low format.
11. Corrections are executed and the same Validator session reviews a new immutable snapshot on the next cycle. When `MissionPolicy.MaxValidationCycles` is reached without approval, the Validator closes and the mission stops in `NeedsHumanAttention`; LoopGolem never escalates above GPT-6 Luna High automatically.

## Luna Low session affinity

Worker-session reuse is a cost/context optimization layered underneath independent mission tasks. The Orchestrator still reasons about separate microtasks; the Worker adapter decides whether a persistent Luna Low thread is safe to resume.

Each completed Worker turn stores a non-authoritative context-reuse hint. `CodexWorkerSessionService` combines that hint with deterministic task-graph and path evidence. Direct dependency and read-after-write relationships favor reuse, while intervening writes into the shared context footprint make a candidate ineligible.

Persistent Worker sessions use leases. A session is leased to a task before external Codex execution and is unavailable to other tasks until that turn has been accepted and finalized. A stale lease after restart invalidates the session, as does an uncommitted last task, missing provider thread identity, task rejection or policy mismatch.

The default affinity policy caps a Worker thread at three accepted microtasks, thirty idle minutes and four active Worker sessions per mission. `SessionReuseMode.Disabled` remains a first-class control that forces fresh ephemeral Worker calls, allowing benchmark comparisons without changing model, reasoning effort or task granularity.

## Deterministic recovery

Known deterministic process failures do not immediately terminate a Codex mission. A completed non-zero `run_command` / .NET build (or a controlled timeout) is persisted as a failed `MissionTaskAttempt` with its process evidence, and the original task enters `RecoveryPending`.

Recovery is provider-independent at the orchestrator boundary through `IMissionRecoveryPlanner`. The Worker adapter implements that contract with the mission's existing persistent GPT-6 Luna High Supervisor. A recovery turn is read-only and produces at most 12 bounded Luna Low repair microtasks. Repair task IDs are namespaced by the failed task and cycle so multiple failed checks cannot collide.

The persisted cycle progresses through `Pending -> Planning -> Repairing -> Retrying`. After repairs complete, the orchestrator marks the cycle `Retrying` and walks the original failed task's dependency graph before making the exact check runnable again. Completed internal `BuildDotNet` ancestors are replayable prerequisites. Planner-produced deterministic `run_command` ancestors are replayable only when their persisted task definition explicitly sets `rerunAfterRepair=true`; the Planner is instructed to use that flag only for safe, idempotent artifact-refresh commands whose outputs are consumed by downstream dependent checks. Arbitrary deterministic commands remain non-replayable by default.

Replayable ancestors are reset to `Planned`, and the original failed task is also returned to `Planned`. Normal dependency scheduling therefore reruns the invalidated prerequisites first and then reruns the original deterministic definition unchanged. The recovery cycle remains scoped to that original failed check. The same failed-task record, deterministic definition, and execution context are reused. `DotNetBuildExecutor` persists the selected solution/project before its first attempt, so a repair cannot alter which target the recheck builds.

If a replayed prerequisite fails, normal deterministic failure handling applies and the original recovery cycle is not falsely marked successful. If the exact recheck fails again, the completed cycle is recorded as failed and a new cycle is created. The mission-level `MaxRecoveryCycles` policy defaults to 3; reaching the limit marks the cycle `Exhausted`, escalates the original task, and stops in `NeedsHumanAttention`. A repair microtask failure is also conservative and stops for human attention instead of recursively spawning another repair tree.

Recovery is restart-safe. Task attempts, cycle status, repair task IDs, failure evidence, Supervisor turns, and task execution contexts are persisted. On Worker restart, a completed deterministic attempt is reconciled before replay: known success completes the task, and known recoverable failure restores `RecoveryPending`. An arbitrary `run_command` that was interrupted without a completed process outcome remains non-replayable because its side effects are unknown.

## Git snapshots

Validation snapshots are Git commit objects created with a temporary alternate index. They are intentionally unreachable: no branch or tag is updated. Git may garbage-collect these objects later.

This gives the validator an immutable `baseCommit..snapshotCommit` diff while leaving the user's working branch untouched.

## Self-hosting rule

A running Worker does not hot-reload changes made to LoopGolem itself. If a mission modifies `LoopGolem.Core`, `LoopGolem.Orchestrator` or `LoopGolem.Worker`, later tasks in that same mission must not assume that new runtime behavior is already active in the current Worker process. Newly built child processes may be used for build/self-test verification, but the orchestrator keeps running the binary with which it started.

## Persistence

The worker owns the SQLite database.

- Windows: `%LOCALAPPDATA%\LoopGolem\loopgolem.db`
- Linux: `$XDG_STATE_HOME/loopgolem/loopgolem.db` or `~/.local/state/loopgolem/loopgolem.db`

SQLite uses WAL mode. UI clients do not open the database directly. Planner tasks, expanded microtask definitions, validator tasks, correction cycles and mutable-task execution contexts are persisted so recovery remains possible across Worker restarts.

Mission capability snapshots are stored separately from the mutable mission snapshot. This prevents normal orchestrator updates from overwriting adapter-level environment evidence and ensures a restarted Worker continues with the same agent/host capability assumptions.

Each mission task has a persisted execution-attempt count plus normalized `mission_task_attempts` rows. Attempt numbers are allocated from the greater of task state and persisted history, which prevents logical-number reuse after restart or database migration. Attempts record running/succeeded/failed/interrupted outcome, failure classification, timestamps, and available evidence.

Completed Codex calls also persist token usage on the task: input, cached input, output, optional reasoning output, and total tokens. Usage is accumulated when a task has more than one completed Codex attempt. Deterministic tasks consume no model tokens. The Desktop shows per-task totals and the aggregate mission total.

The mission Supervisor is a persistent `AgentSession` whose provider thread id is captured from `thread.started`. Planning creates it; later recovery turns resume it. If Codex reports that the exact expected provider session no longer exists, the old logical session is invalidated and a replacement Supervisor is seeded from persisted mission/task/capability state. Generic provider failures are not treated as session loss.

The mission Validator is a distinct persistent `AgentSessionRole.Validator`. The first validation creates it, correction cycles resume it, and no selection path can substitute the Supervisor because session resolution is role-scoped. A missing Validator provider thread invalidates only that Validator and starts a replacement from the current validation prompt. Final acceptance or validation-limit exhaustion closes the Validator session.

Worker `AgentSession` rows additionally persist lease owner, accepted microtask count and termination reason. Worker `AgentTurn` rows persist the model's reuse hint alongside normal token telemetry, which allows benchmark analysis to compare fresh and resumed work by session and turn.

The Worker exposes a read-only mission telemetry summary over IPC. The Desktop shows wall time; input, cached-input, cache-write-input, output, reasoning-output and total tokens; session/turn counts; Worker reuse counts; recovery counts; and per-role totals. The mission's persisted Worker context mode is shown alongside the metrics. The controlled benchmark procedure is defined in `docs/benchmark-v2.md`.

Before a Luna Low task begins, LoopGolem persists a Git workspace baseline. If the Worker stops mid-task, the resumed attempt reuses that original baseline and enters `Retrying`, so edits made before the crash cannot disappear into a new baseline. Deterministic `write_file` and `create_directory` operations are naturally replayable. `rename_path` stores enough pre-execution state to recognize a rename that completed before persistence. An interrupted arbitrary `run_command` is not replayed automatically because its side effects may already have occurred; the mission stops in `NeedsHumanAttention`.

## IPC

Desktop and CLI clients communicate with the worker through the local named pipe `loopgolem-worker-v1`. The protocol is newline-delimited JSON with request/response framing. Client disconnects are transport conditions; they do not stop an in-progress Worker mission.

## Provider and policy boundaries

Codex calls remain worker-side adapters. Mission, task, quota, protocol and policy concepts remain provider-independent in Core.

The current model policy is intentionally bounded:

- GPT-6 Luna High: planning and final validation.
- GPT-6 Luna Low: bounded implementation microtasks.
- Deterministic executor: exact work and local verification with zero model tokens.
- Human attention: the escalation target when Luna High cannot close the mission safely.

LoopGolem does not automatically use Sol, Astra or any model above Luna High. Provider credentials are never injected into prompts. Quota exhaustion is a wait state, and LoopGolem never automatically purchases credits or silently falls back to paid API usage.
