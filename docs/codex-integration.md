# Codex integration

LoopGolem uses the official Codex CLI for bounded planning, implementation and validation tasks.

Agent work is allowed only when `codex login status` reports ChatGPT authentication. LoopGolem never asks for API keys, cookies or access tokens and does not silently fall back to paid API usage.

## Windows: Codex runs inside WSL2

LoopGolem runs Codex inside a user Linux distribution in WSL2 while keeping the Desktop, Worker, Git verification and .NET build on Windows.

The workspace remains on the Windows filesystem. A path such as:

```text
C:\Projects\repo
```

is exposed to Codex as:

```text
/mnt/c/Projects/repo
```

LoopGolem ignores infrastructure-only distributions such as `docker-desktop`. Set `LOOPGOLEM_WSL_DISTRO` to explicitly choose a user distribution when more than one is installed.

Windows onboarding requirements:

1. Install a WSL2 Linux distribution, for example Ubuntu.
2. Install the official Codex CLI inside that distribution.
3. Run `codex` inside the distribution and choose **Sign in with ChatGPT**.
4. Verify inside WSL with `codex login status`.

The Windows Codex CLI installation is not used for autonomous LoopGolem Codex execution.

LoopGolem invokes Codex through `bash -lc` inside the selected distribution so the login-shell PATH is available while arguments remain safely quoted.

## Linux

On Linux, LoopGolem invokes the native Codex CLI directly.

## Model policy

LoopGolem does not inherit the user's Codex default model.

The current autonomous policy is:

```text
planner:   gpt-6-luna / high
worker:    gpt-6-luna / low
validator: gpt-6-luna / high
```

LoopGolem never automatically escalates above GPT-6 Luna High. Repeated validation failure becomes `NeedsHumanAttention`.

## Environment capabilities

Before the first Codex task for a mission, LoopGolem captures and persists a mission-level capability snapshot for two distinct execution environments:

- **agent environment**: where Planner/Luna Low/Validator commands execute. On Windows this is the selected WSL distribution; on Linux it is the native environment.
- **deterministic host environment**: where LoopGolem deterministic `run_command` and local verification executors run. On Windows this is Windows itself.

The initial probe deliberately covers only `git` and `dotnet`, including version text when available. A listed tool marked unavailable is a known capability boundary. A tool that is not listed was not probed and has unknown availability.

The snapshot is persisted once per mission and reused across Worker restarts instead of being silently refreshed. This keeps planning, execution and later benchmark telemetry tied to the same observed environment assumptions.

Planner, Luna Low and Validator prompts receive the snapshot. The planner must not assign a known-unavailable agent tool to Luna Low or place a host-only command in a Luna Low acceptance check. If a required check is available on the deterministic host but unavailable in the agent environment, the planner should schedule deterministic host verification instead. Luna Low and Validator are explicitly told not to retry known-unavailable agent tools.

## Planner / Supervisor

The planner is the first turn of the mission's persistent GPT-6 Luna High Supervisor. It runs read-only with repository-wide visibility. Its hidden contract requires a structured plan of small dependency-aware tasks containing precise read files, write files and acceptance checks.

Keeping this thread alive preserves planning context for later deterministic-failure diagnosis without merging task semantics. Each later Supervisor turn still has its own persisted `agent_turns` record and token telemetry.

The planner chooses between:

- deterministic operations for exact mechanical work;
- GPT-6 Luna Low for small tasks requiring implementation judgment.

A self-hosting rule warns the planner that the currently running LoopGolem Worker does not hot-reload changes made to its own runtime projects.

## Session transport

LoopGolem has three explicit Codex CLI transport modes:

- `FreshEphemeral`: starts a new `codex exec --ephemeral` thread.
- `NewPersistent`: starts a new non-ephemeral Codex thread. When the JSONL stream emits `thread.started`, LoopGolem persists the provider thread id immediately instead of waiting for the Codex process to exit.
- `Resume`: resumes a previously persisted provider thread with `codex exec ... resume <thread-id> -`. The logical LoopGolem session must still be active and its role, model and reasoning effort must match the requested turn.

The transport reads JSONL stdout incrementally. Persistent thread identity is therefore crash-safe once `thread.started` has been observed. Completed turns persist duration and token dimensions in `agent_turns`.

Planner runs as the first turn of one persistent **Supervisor** session per mission. Deterministic-recovery planning resumes this same Supervisor through `Resume`, preserving the reasoning context that created the mission plan. Luna Low workers and the final Validator remain fresh/independent at this stage; their own reuse policies are later steps so those variables stay isolated.

If a Supervisor resume fails with the Codex CLI's explicit `Session not found: <expected-thread-id>` error, LoopGolem invalidates that logical session with reason `provider_session_not_found`, reconstructs mission context from persisted mission/task/capability state, and starts a replacement persistent Supervisor. Other errors such as quota/auth/transient failures do not trigger a session reset. An active Supervisor with no persisted provider thread id, a model/reasoning mismatch, or a duplicate active Supervisor is also invalidated deterministically before selection.

## Deterministic recovery planning

When a deterministic host check completes with a known failure, the orchestrator persists the failed attempt and asks the same read-only GPT-6 Luna High Supervisor for a repair plan. The recovery prompt contains the original user goal, failed deterministic definition, its persisted execution context, failure summary/error, environment capabilities, and a bounded excerpt of the process evidence. Full stdout/stderr evidence remains persisted even when the prompt excerpt is truncated.

The Supervisor may return only a small batch of Luna Low repair tasks (currently at most 12). Repairs use a cycle/task-specific namespace and precise read/write allowlists. The Supervisor is explicitly forbidden from changing, weakening, skipping, or replacing the deterministic check. After repairs, LoopGolem reruns the exact original task; it does not ask the model to decide whether verification is sufficient.

A failed recheck returns to the same Supervisor for the next recovery cycle, up to the mission's configured `MaxRecoveryCycles`. If the Supervisor thread itself has disappeared, the controlled session-reset path reconstructs context from persisted mission tasks, task attempts, recovery cycles, and capability evidence before planning continues.

Luna Low repair calls are still fresh sessions in step 5. Reusing Low context is a separate policy/benchmark variable implemented later.

## Luna Low workers

Each Luna Low call is currently a fresh bounded task. It receives the microtask goal, read files, write allowlist and acceptance checks rather than the entire original mission context.

Codex is instructed not to commit, push, create branches or rewrite Git history. LoopGolem verifies the actual changed files after every worker call and rejects edits outside the task's write allowlist.

## Final validation

After deterministic Git/build verification, LoopGolem creates an unreachable snapshot commit using a temporary Git index. The user's branch and real index are unchanged.

GPT-6 Luna High receives:

- the original user goal;
- the original planner output;
- the original base commit;
- the immutable snapshot commit.

The validator inspects `git diff baseCommit..snapshotCommit` in read-only mode. It returns either:

- `ok`: the mission satisfies the original goal;
- `not_ok`: a bounded correction task batch.

Correction batches use the same deterministic/Luna Low task format. LoopGolem runs at most three validation cycles before stopping for human attention.

## Permission model

Luna Low runs with `workspace-write` sandboxing, network disabled and approval policy `never`. Planning and validation run read-only. Apps, plugins, multi-agent mode and Codex memories are explicitly disabled. Disabling memories keeps future session-reuse experiments focused on thread context rather than a second persistent-memory mechanism.

## Worktree safety

Codex missions require a clean Git working tree before planning begins. This avoids mixing autonomous edits with unrelated local work.

A zero Codex exit code is never accepted as proof by itself. LoopGolem additionally checks structured responses, changed-file allowlists, Git diff validity, local builds when applicable, and final Luna High validation.

Before each Luna Low call, LoopGolem persists a Git workspace baseline in mission state. If the Worker is interrupted, the next attempt reuses that same baseline rather than recapturing the partially modified workspace. This keeps write-allowlist verification meaningful across process restarts.

## Token usage telemetry

LoopGolem invokes autonomous `codex exec` calls with JSON event output enabled and reads token usage from the completed-turn event. It persists input, cached-input, cache-write-input, output, reasoning-output and comparable total-token counts. Cached input is a subset of input and is not added a second time when computing totals. If the CLI does not provide `total_tokens`, LoopGolem computes the call total as input plus output.

Every Codex call also receives a logical session and turn record containing role/purpose, model, reasoning effort, turn number and duration. Recovery Supervisor turns use purpose `Recovery` and are linked from persisted recovery cycles. Calls that still use `FreshEphemeral` close their logical session after the single turn; persistent modes keep the logical session active for later resume.

Task-level token usage remains available for mission execution and Desktop rollups, while `agent_sessions` and `agent_turns` preserve the richer dimensions needed for controlled benchmark analysis. If the Worker or Codex process is terminated before a usage event is returned, LoopGolem does not invent an estimate for that interrupted call; an incomplete turn can remain persisted for recovery analysis.

## Future direction

The CLI transport now has the primitives needed for resumable sessions without changing mission semantics. A future migration to Codex app-server or an official SDK remains possible if LoopGolem later needs richer lifecycle control or quota telemetry that the CLI cannot expose.
