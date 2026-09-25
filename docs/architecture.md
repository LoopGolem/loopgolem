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
2. GPT-6 Luna High runs as a read-only planner. It can inspect the repository and returns a structured dependency graph of small tasks.
3. Exact mechanical work uses local deterministic operations such as `write_file`, `create_directory`, `rename_path` and direct `run_command` execution.
4. Tasks requiring implementation judgment run as fresh GPT-6 Luna Low workers. Each worker receives only its bounded prompt, explicit read files, explicit write allowlist and acceptance checks.
5. LoopGolem verifies that a Luna Low worker did not change files outside its write allowlist.
6. After a task batch, LoopGolem runs deterministic Git inspection and the available local .NET build verification.
7. LoopGolem creates an **unreachable Git snapshot commit** from the working tree using a temporary index. The user's branch, index and HEAD are not moved.
8. GPT-6 Luna High runs again as a read-only validator. It receives the original user goal, original plan, base commit and snapshot commit, and validates the actual diff.
9. If validation returns `ok`, the mission completes. If it returns `not_ok`, the validator may return a small correction task batch in the same deterministic/Luna Low format.
10. Corrections are executed and validated again. After three validator cycles without approval, the mission stops in `NeedsHumanAttention`; LoopGolem never escalates above GPT-6 Luna High automatically.

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

Each mission task has a persisted execution-attempt count, initialized to zero. The count increments before an executor is invoked, and increments again when a task in `Running` or `Retrying` is resumed after a Worker restart. SQLite schema migration adds this persisted state while preserving compatibility with existing databases.

Completed Codex calls also persist token usage on the task: input, cached input, output, optional reasoning output, and total tokens. Usage is accumulated when a task has more than one completed Codex attempt. Deterministic tasks consume no model tokens. The Desktop shows per-task totals and the aggregate mission total.

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
