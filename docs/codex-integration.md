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

## Worker state directory

LoopGolem persists mission/orchestration state in a local SQLite database. The
default state directory is `%LOCALAPPDATA%\\LoopGolem` on Windows and the
platform-specific XDG/local-state path on Linux.

Set `LOOPGOLEM_STATE_DIR` before starting the Worker to use an explicit state
directory. This is intended for isolated tests and controlled benchmarks; it
does not change Codex authentication or the target workspace. All Worker
processes that must share persisted mission/session state need the same value.

## Model policy

LoopGolem does not inherit the user's Codex default model.

The current autonomous model ceiling is GPT-6 Luna High:

```text
planner:   gpt-6-luna / high
worker:    gpt-6-luna / low or high (mission policy)
validator: gpt-6-luna / high
```

The compatibility/default Worker path resolves to Low. Controlled experiments may select High explicitly. LoopGolem never automatically escalates above GPT-6 Luna High. Repeated validation failure becomes `NeedsHumanAttention`.

## Environment capabilities

Before the first Codex task for a mission, LoopGolem captures and persists a mission-level capability snapshot for two distinct execution environments:

- **agent environment**: where Planner/Worker/Validator Codex commands execute. On Windows this is the selected WSL distribution; on Linux it is the native environment.
- **deterministic host environment**: where LoopGolem deterministic `run_command` and local verification executors run. On Windows this is Windows itself.

The initial probe deliberately covers only `git` and `dotnet`, including version text when available. A listed tool marked unavailable is a known capability boundary. A tool that is not listed was not probed and has unknown availability.

The snapshot is persisted once per mission and reused across Worker restarts instead of being silently refreshed. This keeps planning, execution and later benchmark telemetry tied to the same observed environment assumptions.

Planner, Worker and Validator prompts receive the snapshot. The planner must not assign a known-unavailable agent tool to a Worker or place a host-only command in a Worker acceptance check. If a required check is available on the deterministic host but unavailable in the agent environment, the planner should schedule deterministic host verification instead. Worker and Validator prompts are explicitly told not to retry known-unavailable agent tools.

## Planner / Supervisor

The planner is the first turn of the mission's persistent GPT-6 Luna High Supervisor. It runs read-only with repository-wide visibility. Its hidden contract requires a structured plan of small dependency-aware tasks containing precise read files, write files and acceptance checks.

Keeping this thread alive preserves planning context for later deterministic-failure diagnosis without merging task semantics. Each later Supervisor turn still has its own persisted `agent_turns` record and token telemetry.

The planner chooses between:

- deterministic operations for exact mechanical work;
- a bounded GPT-6 Luna Worker for small tasks requiring implementation judgment; mission policy selects Low or High reasoning.

A self-hosting rule warns the planner that the currently running LoopGolem Worker does not hot-reload changes made to its own runtime projects.

## Session transport

The established CLI adapter has three explicit modes:

- `FreshEphemeral`: starts a new `codex exec --ephemeral` thread.
- `NewPersistent`: starts a new non-ephemeral Codex thread. When the JSONL stream emits `thread.started`, LoopGolem persists the provider thread id immediately instead of waiting for the Codex process to exit.
- `Resume`: resumes a previously persisted provider thread with `codex exec ... resume <thread-id> -`. The logical LoopGolem session must still be active and its role, model and reasoning effort must match the requested turn.

The CLI transport reads JSONL stdout incrementally. Persistent thread identity is therefore crash-safe once `thread.started` has been observed. Completed turns persist duration and token dimensions in `agent_turns`.

An experimental fourth execution path, `WorkerContextStrategy.SupervisorFork`, uses `codex app-server --listen stdio://` instead of `codex exec`. It calls `thread/fork` on a persisted Supervisor provider thread, requires the child to report inherited HIGH reasoning, starts the Worker turn with the configured effort through `turn/start`, validates that final effort through `thread/read`, records app-server token-usage notifications, and closes the child after the bounded turn. Apps, plugins, multi-agent mode and Codex memories are disabled for this app-server process.

Planner and Worker calls use the same structured-output schema. This is intentional: C5 showed that changing the structured-output schema was the primary observed cache breaker, while changing role-specific text alone preserved the warmed prefix.

Planner runs as the first turn of one persistent **Supervisor** session per ordinary mission. Deterministic-recovery planning resumes the mission-local Supervisor through `Resume`. Validation uses a separate persistent **Validator** session that is never shared with Supervisor or Worker roles. Worker context strategy is explicit: Fresh uses a one-turn ephemeral CLI session, Affinity uses bounded persistent CLI sessions, and SupervisorFork uses a one-turn app-server child while leaving the parent Supervisor independent.

If a Supervisor resume fails with the Codex CLI's explicit `Session not found: <expected-thread-id>` error, LoopGolem invalidates that logical session with reason `provider_session_not_found`, reconstructs mission context from persisted mission/task/capability state, and starts a replacement persistent Supervisor. Other errors such as quota/auth/transient failures do not trigger a session reset. An active Supervisor with no persisted provider thread id, a model/reasoning mismatch, or a duplicate active Supervisor is also invalidated deterministically before selection.

## Deterministic recovery planning

When a deterministic host check completes with a known failure, the orchestrator persists the failed attempt and asks the same read-only GPT-6 Luna High Supervisor for a repair plan. The recovery prompt contains the original user goal, failed deterministic definition, its persisted execution context, failure summary/error, environment capabilities, and a bounded excerpt of the process evidence. Full stdout/stderr evidence remains persisted even when the prompt excerpt is truncated.

The Supervisor may return only a small batch of Luna Low repair tasks (currently at most 12). Repairs use a cycle/task-specific namespace and precise read/write allowlists. The Supervisor is explicitly forbidden from changing, weakening, skipping, or replacing the deterministic check. After repairs, LoopGolem reruns the exact original task; it does not ask the model to decide whether verification is sufficient.

A failed recheck returns to the same Supervisor for the next recovery cycle, up to the mission's configured `MaxRecoveryCycles`. If the Supervisor thread itself has disappeared, the controlled session-reset path reconstructs context from persisted mission tasks, task attempts, recovery cycles, and capability evidence before planning continues.

Repair Worker calls use the same Worker context/reasoning policy as normal implementation microtasks. They remain separate mission tasks with their own baselines and verification.

## Worker execution policy

Worker work remains task-scoped even when transport context is reused. Every microtask keeps its own persisted task status, execution attempt, Git baseline, token usage, write allowlist, acceptance checks and deterministic post-task verification.

The explicit context choices are `Fresh`, `Affinity` and experimental `SupervisorFork`; the explicit reasoning choices are `Low` and `High`. For compatibility with persisted missions and older clients, a null explicit Worker context derives from legacy `SessionReuseMode`. The default therefore still resolves to Affinity, while legacy `SessionReuseMode.Disabled` resolves to Fresh.

With affinity enabled, the first eligible Worker microtask starts a persistent Worker thread. After a successful turn, the Worker returns a small `contextReuse` hint saying whether its current repository understanding is likely to help an immediate follow-up task. The hint is persisted on the `AgentTurn`, but it is not authoritative.

For a frozen SupervisorFork mission, `SupervisorSourceMissionId` identifies the paused `StopAfterPlanning` mission whose HIGH Supervisor generated the frozen PlannerResult. Before forking, LoopGolem requires the source to exist, remain paused and plan-only, match the measured mission's goal/workspace, expose an active HIGH Supervisor provider thread, and carry a byte-for-byte identical persisted PlannerResult. The measured mission never reruns the Planner and does not resume or mutate the source mission.

LoopGolem reuses a Worker session only when both the model hint and deterministic affinity rules agree. Direct task dependencies and read-after-write file relationships carry the strongest weight. Write/write overlap, shared reads and shared directories can strengthen affinity. Any completed intervening task that writes into the relevant context footprint cancels reuse for that candidate.

Worker sessions are deliberately bounded. The default policy allows at most 3 accepted microtasks per Worker session, 30 minutes of idle time and 4 active Worker sessions per mission. Exceeding a cap closes the session; policy/model changes, missing provider thread ids, stale crash leases, uncommitted prior tasks and rejected worker results invalidate it.

A lease owner task id is persisted before a resumable Worker thread is handed to Codex. The current scheduler is serial, and a leased session is never considered available to another task. If the Worker process dies mid-turn, the stale lease causes the old Worker session to be invalidated on the next attempt; the task retries in a fresh Low session using its already-persisted Git baseline.

If Codex reports that a selected Worker provider thread no longer exists, LoopGolem invalidates that logical Worker session and starts a fresh persistent Worker thread for the same microtask. Other worker failures do not silently trigger context reuse.

Codex is instructed not to commit, push, create branches or rewrite Git history. LoopGolem verifies the actual changed files after every worker call and rejects edits outside the task's write allowlist. A timeout, invalid structured response, blocker, write-allowlist violation or deterministic verification mismatch invalidates that Worker context instead of recycling it.

## Final validation

After deterministic Git/build verification, LoopGolem creates an unreachable snapshot commit using a temporary Git index. The user's branch and real index are unchanged.

The first validation cycle creates a dedicated persistent GPT-6 Luna High Validator session. If validation returns `not_ok` and policy permits another cycle, the next validation resumes that same Validator thread so it retains the review context and the corrections it previously requested. This thread is independent from the persistent Supervisor used for planning/recovery and from all Luna Low Worker sessions.

Persistence does not allow the Validator to treat its own prior conclusion as authoritative. Every cycle receives a newly created immutable snapshot commit and is explicitly instructed to re-inspect the current `baseCommit..snapshotCommit` diff. Prior Validator reasoning is context only.

If the exact Validator provider thread disappears, LoopGolem invalidates only that Validator session and starts a replacement persistent Validator from the current self-contained validation prompt. The Supervisor session is untouched. A valid final `ok` closes the Validator session; a final `not_ok` at the configured validation limit closes it with `validation_limit_reached`. Invalid structured output or failed Validator execution invalidates the session.

GPT-6 Luna High receives:

- the original user goal;
- the original planner output;
- the original base commit;
- the immutable snapshot commit.

The validator inspects `git diff baseCommit..snapshotCommit` in read-only mode. It returns either:

- `ok`: the mission satisfies the original goal;
- `not_ok`: a bounded correction task batch.

Correction batches use the same deterministic/Luna Low task format. LoopGolem uses `MissionPolicy.MaxValidationCycles` (default 3) before stopping for human attention.

## Permission model

Workers run with `workspace-write` sandboxing, network disabled and approval policy `never`. Planning and validation run read-only. Apps, plugins, multi-agent mode and Codex memories are explicitly disabled in both the CLI and experimental SupervisorFork paths. Disabling memories keeps context experiments focused on thread lineage rather than a second persistent-memory mechanism.

## Worktree safety

Codex missions require a clean Git working tree before planning begins. This avoids mixing autonomous edits with unrelated local work.

A zero Codex exit code is never accepted as proof by itself. LoopGolem additionally checks structured responses, changed-file allowlists, Git diff validity, local builds when applicable, and final Luna High validation.

Before each Worker call, LoopGolem persists a Git workspace baseline in mission state. If the Worker is interrupted, the next attempt reuses that same baseline rather than recapturing the partially modified workspace. This keeps write-allowlist verification meaningful across process restarts.

## Token usage telemetry

LoopGolem persists input, cached-input, cache-write-input, output, reasoning-output and comparable total-token counts. The CLI adapter reads usage from JSON completed-turn events; the experimental app-server fork adapter records the corresponding token-usage notifications. Cached input is a subset of input and is not added a second time when computing totals. If a transport does not provide a comparable total directly, LoopGolem computes the call total from the available input/output dimensions.

Every Codex call also receives a logical session and turn record containing role/purpose, model, reasoning effort, turn number and duration. Recovery Supervisor turns use purpose `Recovery` and are linked from persisted recovery cycles. Worker turns additionally persist the model's context-reuse recommendation and reason. Calls using the explicit `FreshEphemeral` control mode close their logical session after the single turn; persistent modes keep the logical session active only while policy allows later resume.

Task-level token usage remains available for mission execution and Desktop rollups, while `agent_sessions` and `agent_turns` preserve the richer dimensions needed for controlled benchmark analysis. If the Worker or Codex process is terminated before a usage event is returned, LoopGolem does not invent an estimate for that interrupted call; an incomplete turn can remain persisted for recovery analysis.

## Future direction

The CLI adapter remains the stable path for fresh, affinity, Supervisor and Validator sessions. The app-server path is intentionally narrow and experimental today: SupervisorFork workers only. A future production adapter may make app-server long-lived and general-purpose, with centralized JSON-RPC routing, richer lineage/lifecycle management and quota telemetry, without changing provider-independent mission semantics.
